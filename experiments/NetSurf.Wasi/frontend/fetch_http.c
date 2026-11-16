/*
 * HTTP-Fetcher für NetSurf auf ZehOS. build.py kopiert die Datei als content/fetchers/zehos_http.c.
 * Die Verbindung liefert der Kernel über zehos_v1.net_*; Weiterleitungen und Cache erledigt llcache.
 * Cookies und HTTP-Anmeldung (401) unterstützt dieser Fetcher noch nicht.
 * HTTPS ist noch nicht angebunden und wird ausdrücklich als nicht unterstützt gemeldet.
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
#define NET_EAGAIN 6
#define NET_EHOSTUNREACH 23
#define NET_EMFILE 33
#define NET_ECONNREFUSED 14
#define NET_ECONNRESET 15
#define NET_ENOENT 44
#define NET_ETIMEDOUT 73

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
	const char *error;      /* vor dem Start erkannter Fehler, z. B. HTTPS */
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

static bool on_header(void *ctx, const char *line, size_t len)
{
	struct http_fetch *f = ctx;
	if (!f->code_set) {
		fetch_set_http_code(f->parent, f->response.status);
		f->code_set = true;
	}
	if (len >= 9 && strncasecmp(line, "Location:", 9) == 0 && f->location == NULL) {
		const char *value = line + 9, *end = line + len;
		while (value < end && (*value == ' ' || *value == '\t')) value++;
		f->location = strndup(value, (size_t)(end - value));
	}
	fetch_msg msg = { .type = FETCH_HEADER };
	msg.data.header_or_data.buf = (const uint8_t *)line;
	msg.data.header_or_data.len = len;
	send_msg(f, &msg);
	return !f->aborted;
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
	if (status == 304 && f->request_len > 0 && strncmp(f->request, "GET ", 4) == 0) {
		msg.type = FETCH_NOTMODIFIED;
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
		f->error = "HTTPS wird von NetSurf auf ZehOS noch nicht unterstützt.";
	} else if (post_multipart != NULL) {
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
		// Kopf und Formulardaten passen zusammen mit den NetSurf-Kopfzeilen in wenigen Kilobyte.
		size_t cap = 4096 + target_len + (post_urlenc != NULL ? strlen(post_urlenc) : 0);
		f->request = malloc(cap);
		if (f->request == NULL) f->error = "Zu wenig Speicher.";
		else if (f->error == NULL) {
			f->request_len = http_request_build(f->request, cap, f->host, f->port,
				target_len > 0 ? target : "/", post_urlenc, headers);
			if (f->request_len == 0) f->error = "Die Anfrage ist zu groß.";
		}
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
		error = zehos_net_open(f->host, (int32_t)strlen(f->host), f->port, 0, &f->handle);
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
