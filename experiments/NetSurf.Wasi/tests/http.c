// Testet den HTTP-Antwortparser nativ, auch mit byteweise zerstückelten Antworten.
#include <assert.h>
#include <stdio.h>
#include <string.h>
#include "../frontend/http_response.h"

struct seen {
	int status;
	int headers;
	char body[256];
	size_t body_len;
	char last_header[128];
};

static bool on_header(void *ctx, const char *line, size_t len) {
	struct seen *s = ctx;
	s->headers++;
	snprintf(s->last_header, sizeof(s->last_header), "%.*s", (int)len, line);
	return true;
}
static bool on_done(void *ctx, int status) { ((struct seen *)ctx)->status = status; return true; }
static bool on_body(void *ctx, const uint8_t *data, size_t len) {
	struct seen *s = ctx;
	assert(s->body_len + len <= sizeof(s->body));
	memcpy(s->body + s->body_len, data, len);
	s->body_len += len;
	return true;
}
static const struct http_callbacks cb = { on_header, on_done, on_body };

// Füttert die Antwort in Stücken der Größe step und liefert das letzte Ergebnis (EOF eingeschlossen, wenn eof).
static enum http_result run(const char *text, size_t step, bool head, uint64_t limit, bool eof, struct seen *s,
	size_t *consumed) {
	static struct http_response r;
	http_response_init(&r, head, limit);
	memset(s, 0, sizeof(*s));
	size_t len = strlen(text), pos = 0, used = 0;
	enum http_result result = HTTP_MORE;
	while (pos < len && result == HTTP_MORE) {
		size_t n = len - pos < step ? len - pos : step;
		result = http_response_feed(&r, (const uint8_t *)text + pos, n, &used, &cb, s);
		pos += used;
	}
	if (consumed != NULL) *consumed = pos;
	if (result == HTTP_MORE && eof) result = http_response_eof(&r);
	return result;
}

static void expect_body(const char *text, const char *body, int status) {
	for (size_t step = 1; step <= strlen(text); step++) {
		struct seen s;
		size_t consumed;
		assert(run(text, step, false, 1024, true, &s, &consumed) == HTTP_DONE);
		assert(s.status == status);
		assert(s.body_len == strlen(body) && memcmp(s.body, body, s.body_len) == 0);
	}
}

int main(void) {
	struct seen s;
	size_t consumed;

	expect_body("HTTP/1.1 200 OK\r\nContent-Length: 5\r\nContent-Type: text/html\r\n\r\nhallo", "hallo", 200);
	// Bytes nach der Content-Length gehören nicht mehr zur Antwort.
	assert(run("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nabXYZ", 64, false, 1024, false, &s, &consumed) == HTTP_DONE);
	assert(s.body_len == 2 && consumed == strlen("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nab"));
	expect_body("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nContent-Length: 99\r\n\r\n"
		"4;ext=1\r\nWiki\r\n5\r\npedia\r\n0\r\nTrailer: x\r\n\r\n", "Wikipedia", 200);
	expect_body("HTTP/1.0 200 OK\nContent-Type: text/plain\n\nbis zum Ende", "bis zum Ende", 200);
	expect_body("HTTP/1.1 100 Continue\r\nX-Zwischen: 1\r\n\r\nHTTP/1.1 404 Not Found\r\nContent-Length: 1\r\n\r\nx", "x", 404);
	assert(run("HTTP/1.1 100 Continue\r\nX-Zwischen: 1\r\n\r\nHTTP/1.1 200 OK\r\nA: b\r\nContent-Length: 0\r\n\r\n",
		3, false, 1024, false, &s, NULL) == HTTP_DONE);
	assert(s.headers == 2 && strcmp(s.last_header, "Content-Length: 0") == 0);

	// Ohne Rumpf: HEAD, 204 und 304 enden nach den Kopfzeilen, auch wenn eine Länge angegeben ist.
	assert(run("HTTP/1.1 200 OK\r\nContent-Length: 10\r\n\r\n", 64, true, 1024, false, &s, NULL) == HTTP_DONE);
	assert(run("HTTP/1.1 304 Not Modified\r\nContent-Length: 10\r\n\r\n", 64, false, 1024, false, &s, NULL) == HTTP_DONE);
	assert(run("HTTP/1.1 301 Moved\r\nLocation: /neu\r\nContent-Length: 0\r\n\r\n", 64, false, 1024, false, &s, NULL) == HTTP_DONE);
	assert(s.status == 301 && strcmp(s.last_header, "Content-Length: 0") == 0);

	// Abgeschnitten: Verbindungsende vor dem Ende von Länge oder Chunks.
	assert(run("HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\nhal", 64, false, 1024, true, &s, NULL) == HTTP_ERR_TRUNCATED);
	assert(run("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nab", 64, false, 1024, true, &s, NULL) == HTTP_ERR_TRUNCATED);
	assert(run("HTTP/1.1 200 OK\r\nContent-Le", 64, false, 1024, true, &s, NULL) == HTTP_ERR_TRUNCATED);

	// Syntaxfehler.
	assert(run("HTTP/2 200 OK\r\n\r\n", 64, false, 1024, false, &s, NULL) == HTTP_ERR_SYNTAX);
	assert(run("HTTP/1.1 20x OK\r\n\r\n", 64, false, 1024, false, &s, NULL) == HTTP_ERR_SYNTAX);
	assert(run("HTTP/1.1 200 OK\r\nKein Doppelpunkt\r\n\r\n", 64, false, 1024, false, &s, NULL) == HTTP_ERR_SYNTAX);
	assert(run("HTTP/1.1 200 OK\r\nA: b\r\n gefaltet\r\n\r\n", 64, false, 1024, false, &s, NULL) == HTTP_ERR_SYNTAX);
	assert(run("HTTP/1.1 200 OK\r\nName : x\r\n\r\n", 64, false, 1024, false, &s, NULL) == HTTP_ERR_SYNTAX);
	assert(run("HTTP/1.1 200 OK\r\nContent-Length: 1\r\nContent-Length: 2\r\n\r\n", 64, false, 1024, false, &s, NULL) == HTTP_ERR_SYNTAX);
	assert(run("HTTP/1.1 200 OK\r\nContent-Length: -1\r\n\r\n", 64, false, 1024, false, &s, NULL) == HTTP_ERR_SYNTAX);
	assert(run("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\nzz\r\n", 64, false, 1024, false, &s, NULL) == HTTP_ERR_SYNTAX);
	assert(run("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n1\r\naX\r\n", 64, false, 1024, false, &s, NULL) == HTTP_ERR_SYNTAX);
	assert(run("HTTP/1.1 200 OK\r\nTransfer-Encoding: gzip\r\n\r\n", 64, false, 1024, false, &s, NULL) == HTTP_ERR_UNSUPPORTED);
	assert(run("HTTP/1.1 101 Switching\r\n\r\n", 64, false, 1024, false, &s, NULL) == HTTP_ERR_UNSUPPORTED);

	// Chunk-Größenzeile: nach Hexziffern und optionalem Leerraum nur ';' oder Zeilenende.
	static const char *const chunk_bad[] = { "5 x", "5x", "5\t1", " 5", "", "0x5", "5,6", "-5", "+5", "5\r", ";5", "5 5" };
	for (size_t i = 0; i < sizeof(chunk_bad) / sizeof(chunk_bad[0]); i++) {
		char text[128];
		snprintf(text, sizeof(text), "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n%s\r\nhallo\r\n0\r\n\r\n", chunk_bad[i]);
		for (size_t step = 1; step <= strlen(text); step++)
			assert(run(text, step, false, 1024, true, &s, NULL) == HTTP_ERR_SYNTAX);
	}
	static const char *const chunk_good[] = { "5", "5 ", "5\t", "5;", "5 ;ext", "5\t;a=b", "5;a=\"x y\"", "00005", "00000000000000000005", "5 \t ;x;y" };
	for (size_t i = 0; i < sizeof(chunk_good) / sizeof(chunk_good[0]); i++) {
		char text[128];
		snprintf(text, sizeof(text), "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n%s\r\nhallo\r\n0\r\n\r\n", chunk_good[i]);
		expect_body(text, "hallo", 200);
	}
	expect_body("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\nA\r\n0123456789\r\n0;ende\r\n\r\n", "0123456789", 200);
	expect_body("HTTP/1.1 200 OK\nTransfer-Encoding: chunked\n\na\n0123456789\n1\n!\n0\n\n", "0123456789!", 200);
	// Bytes nach dem letzten Chunk und den Trailern gehören nicht mehr zur Antwort.
	const char *chunk_tail = "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n2\r\nab\r\n0\r\nT: 1\r\n\r\n";
	char chunk_extra[128];
	snprintf(chunk_extra, sizeof(chunk_extra), "%sHTTP/1.1 200 OK", chunk_tail);
	assert(run(chunk_extra, 64, false, 1024, false, &s, &consumed) == HTTP_DONE);
	assert(consumed == strlen(chunk_tail) && s.body_len == 2);
	// Abgeschnitten nach dem letzten Chunk ohne abschließende Leerzeile bzw. ohne Chunk-Ende.
	assert(run("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n", 64, false, 1024, true, &s, NULL) == HTTP_ERR_TRUNCATED);
	assert(run("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n0\r\nT: 1\r\n", 64, false, 1024, true, &s, NULL) == HTTP_ERR_TRUNCATED);
	assert(run("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n2\r\nab", 64, false, 1024, true, &s, NULL) == HTTP_ERR_TRUNCATED);
	// Chunk-Rahmen zählt zum Rumpflimit: eine lange Erweiterung überschreitet es auch ohne Daten.
	char long_ext[128];
	snprintf(long_ext, sizeof(long_ext), "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n1;%040d\r\n", 0);
	assert(run(long_ext, 64, false, 16, false, &s, NULL) == HTTP_ERR_LIMIT);
	// Rahmen und Daten ergeben hier genau 11 Bytes: mit Limit 11 erlaubt, mit 10 nicht.
	const char *chunk_exact = "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n3\r\nabc\r\n0\r\n\r\n";
	assert(run(chunk_exact, 1, false, 11, true, &s, NULL) == HTTP_DONE && s.body_len == 3);
	assert(run(chunk_exact, 1, false, 10, true, &s, NULL) == HTTP_ERR_LIMIT);
	assert(run("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n9\r\n", 1, false, 11, false, &s, NULL) == HTTP_ERR_LIMIT);
	assert(run("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\nffffffffffffffff\r\n", 64, false, 1024, false, &s, NULL) == HTTP_ERR_LIMIT);

	// Grenzen: Rumpf, Chunkgröße mit Überlauf, Kopfzeilenlänge und -anzahl.
	assert(run("HTTP/1.1 200 OK\r\nContent-Length: 1025\r\n\r\n", 64, false, 1024, false, &s, NULL) == HTTP_ERR_LIMIT);
	assert(run("HTTP/1.1 200 OK\r\nContent-Length: 99999999999999999999999\r\n\r\n", 64, false, 1024, false, &s, NULL) == HTTP_ERR_LIMIT);
	assert(run("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\nffffffffffffffffff\r\n", 64, false, 1024, false, &s, NULL) == HTTP_ERR_LIMIT);
	assert(run("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n401\r\n", 64, false, 1024, false, &s, NULL) == HTTP_ERR_LIMIT);
	static char big[HTTP_LINE_MAX + 64];
	snprintf(big, sizeof(big), "HTTP/1.1 200 OK\r\nX: %0*d\r\n\r\n", HTTP_LINE_MAX, 0);
	assert(run(big, 4096, false, 1024, false, &s, NULL) == HTTP_ERR_LIMIT);
	static char many[HTTP_HEADER_COUNT_MAX * 8 + 64];
	size_t at = (size_t)snprintf(many, sizeof(many), "HTTP/1.1 200 OK\r\n");
	for (int i = 0; i <= HTTP_HEADER_COUNT_MAX; i++) at += (size_t)snprintf(many + at, sizeof(many) - at, "A: %d\r\n", i % 10);
	assert(run(many, 64, false, 1024, false, &s, NULL) == HTTP_ERR_LIMIT);
	char stream[64];
	assert(run("HTTP/1.1 200 OK\r\n\r\n", 64, false, 4, false, &s, NULL) == HTTP_MORE);
	snprintf(stream, sizeof(stream), "HTTP/1.1 200 OK\r\n\r\n12345");
	assert(run(stream, 64, false, 4, true, &s, NULL) == HTTP_ERR_LIMIT);

	// Anfragen.
	char request[512];
	const char *extra[] = { "Accept-Language: de", "Connection: keep-alive", "X-Bad: a\r\nInjected: 1", NULL };
	size_t n = http_request_build(request, sizeof(request), "10.0.2.2", 18080, 80, "/a?b=1", NULL, extra);
	assert(n == strlen(request));
	assert(strcmp(request, "GET /a?b=1 HTTP/1.1\r\nHost: 10.0.2.2:18080\r\nConnection: close\r\n"
		"Accept-Encoding: identity\r\nAccept-Language: de\r\n\r\n") == 0);
	n = http_request_build(request, sizeof(request), "beispiel", 80, 80, "/", "name=x", NULL);
	assert(strstr(request, "POST / HTTP/1.1\r\nHost: beispiel\r\n") == request);
	assert(strstr(request, "Content-Length: 6\r\n\r\nname=x") != NULL);
	assert(http_request_build(request, 20, "beispiel", 80, 80, "/", NULL, NULL) == 0);
	// HTTPS: Standardport 443 fehlt im Host, Port 80 ist dort kein Standard und bleibt stehen.
	http_request_build(request, sizeof(request), "beispiel", 443, 443, "/", NULL, NULL);
	assert(strstr(request, "GET / HTTP/1.1\r\nHost: beispiel\r\n") == request);
	http_request_build(request, sizeof(request), "beispiel", 80, 443, "/", NULL, NULL);
	assert(strstr(request, "Host: beispiel:80\r\n") != NULL);

	puts("http tests ok");
	return 0;
}
