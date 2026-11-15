/*
 * HTTP-Fetcher für NetSurf auf ZehOS. build.py kopiert die Datei als content/fetchers/zehos_http.c.
 * Die Verbindung liefert der Kernel über zehos_v1.net_*; Weiterleitungen und Cache erledigt llcache.
 * Cookies verwaltet urldb wie beim curl-Fetcher; HTTP-Anmeldung (401) unterstützt dieser Fetcher noch nicht.
 * HTTPS verschlüsselt und prüft der Kernel (net_open mit ZEHOS_NET_TLS); ein älterer Kernel meldet ENOTSUP.
 * Zertifikatsfehler lassen sich nicht übergehen; eine Weiterleitung von HTTPS auf HTTP folgt nur nach Klick.
 */
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>
#include <time.h>
#include <libwapcaplet/libwapcaplet.h>

#include "utils/corestrings.h"
#include "utils/log.h"
#include "utils/nsurl.h"
#include "utils/ring.h"
#include "content/fetch.h"
#include "content/urldb.h"
#include "content/fetchers.h"
#include "http_response.h"

#define ZEHOS_IMPORT(name) __attribute__((import_module("zehos_v1"), import_name(name)))
ZEHOS_IMPORT("net_open") int32_t zehos_net_open(const char *host, int32_t host_len, int32_t port, int32_t flags, int32_t *handle);
ZEHOS_IMPORT("net_status") int32_t zehos_net_status(int32_t handle);
ZEHOS_IMPORT("net_send") int32_t zehos_net_send(int32_t handle, const void *data, int32_t len, int32_t *sent);
ZEHOS_IMPORT("net_recv") int32_t zehos_net_recv(int32_t handle, void *data, int32_t len, int32_t *received);
ZEHOS_IMPORT("net_close") int32_t zehos_net_close(int32_t handle);

/* WASI-errno-Werte, die net_* liefern kann. */
#define NET_EACCES 2
#define NET_ENOSYS 52
#define NET_ENOTSUP 58
#define NET_EPERM 63
#define NET_EPROTO 65
#define NET_EAGAIN 6
#define NET_EHOSTUNREACH 23
#define NET_EMFILE 33
#define NET_ECONNREFUSED 14
#define NET_ECONNRESET 15
#define NET_ENOENT 44
#define NET_ETIMEDOUT 73

#define ZEHOS_NET_TLS 1

#define BODY_LIMIT (8u * 1024 * 1024)
#define IDLE_TIMEOUT_MS 30000
#define NET_MAX_SEND (64u * 1024u)
#define RECV_CHUNK 16384

enum { ST_OPEN, ST_CONNECTING, ST_SENDING, ST_RECEIVING, ST_FINISHED };

struct http_fetch {
	struct fetch *parent;
	nsurl *url;
	int state;
	int32_t handle;
	bool only_2xx;
	bool aborted;
	bool started;        /* NetSurf hat start aufgerufen; davor liegt der Abruf nur in der Warteschlange */
	bool locked;
	bool code_set;
	bool tls;
	const char *error;      /* vor dem Start erkannter Fehler, z. B. Dateiupload */
	char host[256];
	int port;
	char *request;
	size_t request_len;
	size_t sent;
	char *location;
	uint64_t last_activity;
	struct http_response response;
	struct http_fetch *r_next, *r_prev;
};

static struct http_fetch *ring;

static uint64_t now_ms(void)
{
	struct timespec ts;
	clock_gettime(CLOCK_MONOTONIC, &ts);
	return (uint64_t)ts.tv_sec * 1000 + (uint64_t)ts.tv_nsec / 1000000;
}

static const char *net_error(int32_t error)
{
	switch (error) {
	case NET_EACCES: return "Diese App hat keine Netzwerkberechtigung.";
	case NET_ENOENT: return "Der Servername wurde nicht gefunden.";
	case NET_ECONNREFUSED: return "Der Server hat die Verbindung abgelehnt.";
	case NET_ECONNRESET: return "Der Server hat die Verbindung abgebrochen.";
	case NET_EHOSTUNREACH: return "Der Server ist nicht erreichbar.";
	case NET_ETIMEDOUT: return "Zeitüberschreitung beim Verbindungsaufbau.";
	case NET_EMFILE: return "Zu viele gleichzeitige Verbindungen.";
	case NET_ENOTSUP: return "HTTPS wird von diesem ZehOS-Kernel noch nicht unterstützt.";
	case NET_ENOSYS: return "HTTPS ist nicht möglich: Systemuhr oder sicherer CPU-Zufall fehlen.";
	case NET_EPERM: return "Das Zertifikat des Servers wurde abgelehnt; die Verbindung ist nicht sicher.";
	case NET_EPROTO: return "Die sichere Verbindung (TLS) ist fehlgeschlagen.";
	default: return "Netzwerkfehler.";
	}
}

static const char *parse_error(enum http_result result)
{
	switch (result) {
	case HTTP_ERR_LIMIT: return "Die Antwort des Servers überschreitet die Größengrenze.";
	case HTTP_ERR_TRUNCATED: return "Die Antwort des Servers ist unvollständig.";
	case HTTP_ERR_UNSUPPORTED: return "Der Server verwendet eine nicht unterstützte Übertragungsart.";
	default: return "Die Antwort des Servers ist kein gültiges HTTP.";
	}
}

static void send_msg(struct http_fetch *f, const fetch_msg *msg)
{
	f->locked = true;
	fetch_send_callback(msg, f->parent);
	f->locked = false;
}

/* Beendet den Abruf mit einer Meldung; type FETCH_FINISHED, FETCH_ERROR oder FETCH_TIMEDOUT. */
static void finish(struct http_fetch *f, fetch_msg_type type, const char *error)
{
	if (f->handle >= 0) {
		zehos_net_close(f->handle);
		f->handle = -1;
	}
	f->state = ST_FINISHED;
	if (f->aborted) return;
	fetch_msg msg = { .type = type };
	msg.data.error = error;
	send_msg(f, &msg);
}

/* Liefert den Wert einer Kopfzeile "Name: Wert" ohne umgebende Leerzeichen oder NULL bei anderem Namen. */
static char *header_value(const char *line, size_t len, const char *name)
{
	size_t name_len = strlen(name);
	if (len < name_len || strncasecmp(line, name, name_len) != 0) return NULL;
	const char *value = line + name_len, *end = line + len;
	while (value < end && (*value == ' ' || *value == '\t')) value++;
	while (end > value && (end[-1] == ' ' || end[-1] == '\t')) end--;
	return strndup(value, (size_t)(end - value));
}

static bool on_header(void *ctx, const char *line, size_t len)
{
	struct http_fetch *f = ctx;
	if (!f->code_set) {
		fetch_set_http_code(f->parent, f->response.status);
		f->code_set = true;
	}
	if (f->location == NULL) f->location = header_value(line, len, "Location:");
	char *cookie = header_value(line, len, "Set-Cookie:");
	if (cookie != NULL) {
		// fetch_set_cookie prüft Verifizierbarkeit und Referer wie beim curl-Fetcher.
		fetch_set_cookie(f->parent, cookie);
		free(cookie);
	}
	// Bei HTTPS-Weiterleitungen erst in on_headers_done entscheiden, ob eine eigene Warnseite die Antwort ersetzt.
	if (f->tls && f->response.status >= 300 && f->response.status < 400 && f->response.status != 304) return true;
	fetch_msg msg = { .type = FETCH_HEADER };
	msg.data.header_or_data.buf = (const uint8_t *)line;
	msg.data.header_or_data.len = len;
	send_msg(f, &msg);
	return !f->aborted;
}

/* Hängt text HTML-maskiert an out an; Rückgabe false, wenn cap nicht reicht. */
static bool append_escaped(char *out, size_t cap, size_t *used, const char *text)
{
	for (; *text != '\0'; text++) {
		const char *piece;
		char single[2] = { *text, '\0' };
		switch (*text) {
		case '&': piece = "&amp;"; break;
		case '<': piece = "&lt;"; break;
		case '>': piece = "&gt;"; break;
		case '"': piece = "&quot;"; break;
		case '\'': piece = "&#39;"; break;
		default: piece = single; break;
		}
		size_t len = strlen(piece);
		if (len >= cap - *used) return false;
		memcpy(out + *used, piece, len);
		*used += len;
	}
	out[*used] = '\0';
	return true;
}

/* Ersetzt eine Weiterleitung von HTTPS auf HTTP durch eine Warnseite; erst ein Klick lädt das unverschlüsselte Ziel. */
static bool send_downgrade_page(struct http_fetch *f, nsurl *target)
{
	static const char head[] = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Unsichere Weiterleitung</title>"
		"<style>body{background:#fff0d8}</style></head><body><h1>Unsichere Weiterleitung</h1><p>Diese sichere Seite leitet auf eine unverschlüsselte "
		"HTTP-Adresse weiter:</p><p>";
	static const char middle[] = "</p><p><a href=\"";
	static const char tail[] = "\">Trotzdem fortfahren</a></p></body></html>";
	char *url_s;
	size_t url_len;
	if (nsurl_get(target, NSURL_COMPLETE, &url_s, &url_len) != NSERROR_OK) return false;
	size_t cap = sizeof(head) + sizeof(middle) + sizeof(tail) + 2 * 6 * url_len;
	char *page = malloc(cap);
	size_t used = 0;
	bool ok = page != NULL;
	if (ok) {
		memcpy(page, head, sizeof(head) - 1);
		used = sizeof(head) - 1;
		ok = append_escaped(page, cap, &used, url_s)
			&& used + sizeof(middle) <= cap;
	}
	if (ok) {
		memcpy(page + used, middle, sizeof(middle) - 1);
		used += sizeof(middle) - 1;
		ok = append_escaped(page, cap, &used, url_s) && used + sizeof(tail) <= cap;
	}
	free(url_s);
	if (!ok) {
		free(page);
		return false;
	}
	memcpy(page + used, tail, sizeof(tail) - 1);
	used += sizeof(tail) - 1;
	fetch_set_http_code(f->parent, 200);
	static const char *const headers[] = { "Content-Type: text/html; charset=utf-8", "Cache-Control: no-store" };
	for (size_t i = 0; i < sizeof(headers) / sizeof(headers[0]) && !f->aborted; i++) {
		fetch_msg msg = { .type = FETCH_HEADER };
		msg.data.header_or_data.buf = (const uint8_t *)headers[i];
		msg.data.header_or_data.len = strlen(headers[i]);
		send_msg(f, &msg);
	}
	if (!f->aborted) {
		fetch_msg msg = { .type = FETCH_DATA };
		msg.data.header_or_data.buf = (const uint8_t *)page;
		msg.data.header_or_data.len = used;
		send_msg(f, &msg);
	}
	free(page);
	return true;
}

/* Prüft, ob location relativ zu url auf eine http://-Adresse zeigt; *target bleibt dann referenziert. */
static bool is_downgrade(nsurl *url, const char *location, nsurl **target)
{
	if (nsurl_join(url, location, target) != NSERROR_OK) return false;
	if (nsurl_get_scheme_type(*target) == NSURL_SCHEME_HTTP) return true;
	nsurl_unref(*target);
	*target = NULL;
	return false;
}

/* Entspricht fetch_curl_process_headers: 304, Weiterleitung und only_2xx enden ohne Rumpf. */
static bool on_headers_done(void *ctx, int status)
{
	struct http_fetch *f = ctx;
	if (!f->code_set) {
		fetch_set_http_code(f->parent, status);
		f->code_set = true;
	}
	fetch_msg msg;
	nsurl *target = NULL;
	if (status == 304 && f->request_len > 0 && strncmp(f->request, "GET ", 4) == 0) {
		msg.type = FETCH_NOTMODIFIED;
	} else if (f->tls && status >= 300 && status < 400 && f->location != NULL
		&& is_downgrade(f->url, f->location, &target)) {
		bool sent = send_downgrade_page(f, target);
		nsurl_unref(target);
		msg.type = sent ? FETCH_FINISHED : FETCH_ERROR;
		msg.data.error = sent ? NULL : "Zu wenig Speicher.";
	} else if (status >= 300 && status < 400 && f->location != NULL) {
		msg.type = FETCH_REDIRECT;
		msg.data.redirect = f->location;
	} else if (f->only_2xx && (status < 200 || status > 299)) {
		msg.type = FETCH_ERROR;
		msg.data.error = "Der Server meldet einen Fehler.";
	} else {
		return !f->aborted;
	}
	if (f->handle >= 0) {
		zehos_net_close(f->handle);
		f->handle = -1;
	}
	f->state = ST_FINISHED;
	if (!f->aborted) send_msg(f, &msg);
	return false;
}

static bool on_body(void *ctx, const uint8_t *data, size_t len)
{
	struct http_fetch *f = ctx;
	fetch_msg msg = { .type = FETCH_DATA };
	msg.data.header_or_data.buf = data;
	msg.data.header_or_data.len = len;
	send_msg(f, &msg);
	return !f->aborted;
}

static const struct http_callbacks callbacks = { on_header, on_headers_done, on_body };

static bool fetch_http_initialise(lwc_string *scheme) { return true; }
static void fetch_http_finalise(lwc_string *scheme) { }
static bool fetch_http_acceptable(const nsurl *url) { return true; }

static void *fetch_http_setup(struct fetch *parent, nsurl *url, bool only_2xx, bool downgrade_tls,
	const char *post_urlenc, const struct fetch_multipart_data *post_multipart, const char **headers)
{
	struct http_fetch *f = calloc(1, sizeof(*f));
	if (f == NULL) return NULL;
	f->parent = parent;
	f->url = nsurl_ref(url);
	f->only_2xx = only_2xx;
	f->handle = -1;
	f->port = 80;
	http_response_init(&f->response, false, BODY_LIMIT);

	lwc_string *host = nsurl_get_component(url, NSURL_HOST);
	lwc_string *port = nsurl_get_component(url, NSURL_PORT);
	char *target = NULL;
	size_t target_len = 0;
	if (nsurl_get_scheme_type(url) == NSURL_SCHEME_HTTPS) {
		f->tls = true;
		f->port = 443;
	}
	if (post_multipart != NULL) {
		f->error = "Formulare mit Dateiupload werden noch nicht unterstützt.";
	} else if (host == NULL || lwc_string_length(host) == 0 || lwc_string_length(host) >= sizeof(f->host)
		|| lwc_string_data(host)[0] == '[') {
		f->error = "Ungültiger oder nicht unterstützter Servername.";
	} else if (nsurl_get(url, NSURL_PATH | NSURL_QUERY, &target, &target_len) != NSERROR_OK) {
		f->error = "Ungültige Adresse.";
	} else {
		memcpy(f->host, lwc_string_data(host), lwc_string_length(host));
		if (port != NULL) {
			char *end;
			long value = strtol(lwc_string_data(port), &end, 10);
			if (end == lwc_string_data(port) || value < 1 || value > 65535) f->error = "Ungültiger Port.";
			f->port = (int)value;
		}
		// Wie curl auch HttpOnly-Cookies senden; die Zeile hängt als letzte Kopfzeile an NetSurfs Liste.
		char *cookies = f->error == NULL ? urldb_get_cookie(url, true) : NULL;
		size_t count = 0;
		while (headers != NULL && headers[count] != NULL) count++;
		const char **all = calloc(count + 2, sizeof(*all));
		char *cookie_line = cookies != NULL ? malloc(strlen(cookies) + 9) : NULL;
		if (cookie_line != NULL) sprintf(cookie_line, "Cookie: %s", cookies);
		// Kopf und Formulardaten passen zusammen mit den NetSurf-Kopfzeilen in wenigen Kilobyte.
		size_t cap = 4096 + target_len + (post_urlenc != NULL ? strlen(post_urlenc) : 0)
			+ (cookies != NULL ? strlen(cookies) : 0);
		f->request = malloc(cap);
		if (f->request == NULL || all == NULL || (cookies != NULL && cookie_line == NULL)) f->error = "Zu wenig Speicher.";
		else if (f->error == NULL) {
			if (count > 0) memcpy(all, headers, count * sizeof(*all));
			all[count] = cookie_line;
			f->request_len = http_request_build(f->request, cap, f->host, f->port,
				target_len > 0 ? target : "/", post_urlenc, all);
			if (f->request_len == 0) f->error = "Die Anfrage ist zu groß.";
		}
		free(cookie_line);
		free(all);
		free(cookies);
	}
	free(target);
	if (host != NULL) lwc_string_unref(host);
	if (port != NULL) lwc_string_unref(port);
	RING_INSERT(ring, f);
	return f;
}

static bool fetch_http_start(void *ctx)
{
	struct http_fetch *f = ctx;
	f->started = true;
	f->last_activity = now_ms();
	return true;
}

static void fetch_http_abort(void *ctx)
{
	// Wie beim data:-Fetcher räumt erst poll auf, damit kein Kontext unter einem Rückruf verschwindet.
	((struct http_fetch *)ctx)->aborted = true;
}

static void fetch_http_free(void *ctx)
{
	struct http_fetch *f = ctx;
	if (f->handle >= 0) zehos_net_close(f->handle);
	nsurl_unref(f->url);
	free(f->request);
	free(f->location);
	free(f);
}

/* Führt einen Schritt aus; Rückgabe true, solange der Abruf weiterläuft. */
static bool step(struct http_fetch *f)
{
	int32_t error;
	if (f->error != NULL) {
		finish(f, FETCH_ERROR, f->error);
		return false;
	}
	if (now_ms() - f->last_activity > IDLE_TIMEOUT_MS) {
		finish(f, FETCH_TIMEDOUT, "Der Server antwortet nicht.");
		return false;
	}
	switch (f->state) {
	case ST_OPEN:
		error = zehos_net_open(f->host, (int32_t)strlen(f->host), f->port, f->tls ? ZEHOS_NET_TLS : 0, &f->handle);
		if (error != 0) {
			f->handle = -1;
			finish(f, FETCH_ERROR, net_error(error));
			return false;
		}
		f->state = ST_CONNECTING;
		return true;
	case ST_CONNECTING:
		error = zehos_net_status(f->handle);
		if (error == NET_EAGAIN) return true;
		if (error != 0) {
			finish(f, FETCH_ERROR, net_error(error));
			return false;
		}
		f->state = ST_SENDING;
		f->last_activity = now_ms();
		return true;
	case ST_SENDING: {
		int32_t sent = 0;
		size_t chunk = f->request_len - f->sent;
		if (chunk > NET_MAX_SEND) chunk = NET_MAX_SEND; /* Kernel: höchstens 64 KiB je net_send */
		error = zehos_net_send(f->handle, f->request + f->sent, (int32_t)chunk, &sent);
		if (error == NET_EAGAIN) return true;
		if (error != 0 || sent < 0 || (size_t)sent > chunk) {
			finish(f, FETCH_ERROR, net_error(error));
			return false;
		}
		if (sent > 0) f->last_activity = now_ms();
		f->sent += (size_t)sent;
		if (f->sent == f->request_len) f->state = ST_RECEIVING;
		return true;
	}
	case ST_RECEIVING: {
		static uint8_t buffer[RECV_CHUNK];
		// Begrenzte Runden je poll, damit ein schneller Server die Oberfläche nicht blockiert.
		for (int round = 0; round < 8; round++) {
			int32_t received = 0;
			error = zehos_net_recv(f->handle, buffer, sizeof(buffer), &received);
			if (error == NET_EAGAIN) return true;
			// Der Kernel meldet TLS-Ende ohne close_notify als ECONNRESET; ohne Längenangabe ist die Antwort abgeschnitten.
			if (error == NET_ECONNRESET && f->tls) {
				finish(f, FETCH_ERROR, parse_error(HTTP_ERR_TRUNCATED));
				return false;
			}
			if (error != 0 || received < 0 || received > (int32_t)sizeof(buffer)) {
				finish(f, FETCH_ERROR, net_error(error));
				return false;
			}
			enum http_result result;
			if (received == 0) {
				result = http_response_eof(&f->response);
			} else {
				f->last_activity = now_ms();
				result = http_response_feed(&f->response, buffer, (size_t)received, NULL, &callbacks, f);
			}
			if (f->state == ST_FINISHED || f->aborted) return false;
			if (result == HTTP_DONE) {
				finish(f, FETCH_FINISHED, NULL);
				return false;
			}
			if (result != HTTP_MORE) {
				finish(f, FETCH_ERROR, parse_error(result));
				return false;
			}
		}
		return true;
	}
	default:
		return false;
	}
}

static void fetch_http_poll(lwc_string *scheme)
{
	struct http_fetch *f, *keep = NULL;
	while (ring != NULL) {
		f = ring;
		RING_REMOVE(ring, f);
		// setup legt jeden Abruf an, NetSurf startet aber höchstens max_fetchers davon. Wartende dürfen weder
		// eine Verbindung öffnen (Kernelgrenze 4) noch an der Leerlauffrist ohne gesetztes last_activity scheitern.
		if (f->locked || (!f->aborted && (!f->started || step(f)))) {
			RING_INSERT(keep, f);
			continue;
		}
		fetch_remove_from_queues(f->parent);
		fetch_free(f->parent);
	}
	ring = keep;
}

/* Aufruf aus fetcher_init (patches/netsurf-zehos-http.patch); dort ohne eigenen Header deklariert. */
nserror fetch_zehos_http_register(void);

nserror fetch_zehos_http_register(void)
{
	const struct fetcher_operation_table ops = {
		.initialise = fetch_http_initialise,
		.acceptable = fetch_http_acceptable,
		.setup = fetch_http_setup,
		.start = fetch_http_start,
		.abort = fetch_http_abort,
		.free = fetch_http_free,
		.poll = fetch_http_poll,
		.finalise = fetch_http_finalise
	};
	nserror error = fetcher_add(lwc_string_ref(corestring_lwc_http), &ops);
	if (error != NSERROR_OK) return error;
	return fetcher_add(lwc_string_ref(corestring_lwc_https), &ops);
}
