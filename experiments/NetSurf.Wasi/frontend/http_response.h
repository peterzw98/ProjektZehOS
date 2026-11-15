/* Begrenzter HTTP/1.1-Antwortparser für den ZehOS-Fetcher; ohne NetSurf-Abhängigkeit, damit er nativ testbar bleibt. */
#ifndef ZEHOS_HTTP_RESPONSE_H
#define ZEHOS_HTTP_RESPONSE_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#define HTTP_LINE_MAX 8192
#define HTTP_HEADER_BYTES_MAX 65536
#define HTTP_HEADER_COUNT_MAX 128

enum http_result {
	HTTP_MORE = 0,       /* weitere Daten nötig */
	HTTP_DONE = 1,       /* Antwort vollständig; folgende Bytes gehören nicht mehr dazu */
	HTTP_ERR_SYNTAX = -1,
	HTTP_ERR_LIMIT = -2, /* Zeile, Kopf oder Rumpf zu groß */
	HTTP_ERR_TRUNCATED = -3,
	HTTP_ERR_UNSUPPORTED = -4, /* unbekannte Transfer-Codierung */
	HTTP_ERR_CALLBACK = -5
};

struct http_callbacks {
	/* Eine Kopfzeile "Name: Wert" ohne CRLF; nicht nullterminiert. Rückgabe false bricht ab. */
	bool (*header)(void *ctx, const char *line, size_t len);
	/* Alle Kopfzeilen gelesen; status ist der endgültige Code (keine 1xx). */
	bool (*headers_done)(void *ctx, int status);
	bool (*body)(void *ctx, const uint8_t *data, size_t len);
};

struct http_response {
	int state;
	int status;
	bool head_request;
	bool chunked;
	bool have_length;
	uint64_t remaining;      /* Restlänge von Rumpf oder aktuellem Chunk */
	uint64_t body_limit;
	uint64_t body_bytes;
	size_t header_bytes;
	unsigned header_count;
	size_t line_len;
	char line[HTTP_LINE_MAX];
};

void http_response_init(struct http_response *r, bool head_request, uint64_t body_limit);
/* Verarbeitet empfangene Bytes; *used meldet, wie viele davon zur Antwort gehörten. */
enum http_result http_response_feed(struct http_response *r, const uint8_t *data, size_t len, size_t *used,
	const struct http_callbacks *cb, void *ctx);
/* Verbindungsende: beendet einen Rumpf ohne Längenangabe, sonst ist die Antwort abgeschnitten. */
enum http_result http_response_eof(struct http_response *r);

/* Schreibt eine GET- bzw. POST-Anfrage; liefert die Länge oder 0, wenn der Puffer nicht reicht. */
size_t http_request_build(char *out, size_t cap, const char *host, int port, const char *target,
	const char *post_body, const char *const *extra_headers);

#endif
