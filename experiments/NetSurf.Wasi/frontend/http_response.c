/* Begrenzter HTTP/1.1-Antwortparser; siehe http_response.h. */
#include "http_response.h"

#include <stdio.h>
#include <string.h>
#include <strings.h>

enum { S_STATUS, S_HEADER, S_BODY_LENGTH, S_BODY_CLOSE, S_CHUNK_SIZE, S_CHUNK_DATA, S_CHUNK_END, S_TRAILER, S_DONE };

void http_response_init(struct http_response *r, bool head_request, uint64_t body_limit)
{
	memset(r, 0, sizeof(*r));
	r->state = S_STATUS;
	r->head_request = head_request;
	r->body_limit = body_limit;
}

static bool is_ows(char c) { return c == ' ' || c == '\t'; }

static void trim(const char **start, const char **end)
{
	while (*start < *end && is_ows(**start)) (*start)++;
	while (*end > *start && is_ows((*end)[-1])) (*end)--;
}

static enum http_result parse_status(struct http_response *r, const char *line, size_t len)
{
	if (len < 12 || memcmp(line, "HTTP/1.", 7) != 0 || (line[7] != '0' && line[7] != '1') || line[8] != ' ')
		return HTTP_ERR_SYNTAX;
	int status = 0;
	for (int i = 9; i < 12; i++) {
		if (line[i] < '0' || line[i] > '9') return HTTP_ERR_SYNTAX;
		status = status * 10 + (line[i] - '0');
	}
	if (status < 100 || (len > 12 && line[12] != ' ')) return HTTP_ERR_SYNTAX;
	// 101 würde das Protokoll wechseln; der Fetcher fragt nie danach.
	if (status == 101) return HTTP_ERR_UNSUPPORTED;
	r->status = status;
	r->state = S_HEADER;
	return HTTP_MORE;
}

static enum http_result parse_length(struct http_response *r, const char *value, const char *end)
{
	uint64_t length = 0;
	if (value == end) return HTTP_ERR_SYNTAX;
	for (const char *p = value; p < end; p++) {
		if (*p < '0' || *p > '9') return HTTP_ERR_SYNTAX;
		if (length > (UINT64_MAX - 9) / 10) return HTTP_ERR_LIMIT;
		length = length * 10 + (uint64_t)(*p - '0');
	}
	// Abweichende doppelte Längen sind ein Zeichen für Request-Smuggling oder einen defekten Server.
	if (r->have_length && r->remaining != length) return HTTP_ERR_SYNTAX;
	r->have_length = true;
	r->remaining = length;
	return HTTP_MORE;
}

static enum http_result parse_header(struct http_response *r, const char *line, size_t len,
	const struct http_callbacks *cb, void *ctx)
{
	if (len == 0) return HTTP_DONE;
	// Gefaltete Kopfzeilen (obs-fold) sind veraltet und werden wie in RFC 9112 abgelehnt.
	if (is_ows(line[0])) return HTTP_ERR_SYNTAX;
	const char *colon = memchr(line, ':', len);
	if (colon == NULL || colon == line || is_ows(colon[-1])) return HTTP_ERR_SYNTAX;
	if (++r->header_count > HTTP_HEADER_COUNT_MAX) return HTTP_ERR_LIMIT;
	const char *value = colon + 1, *end = line + len;
	trim(&value, &end);
	size_t name_len = (size_t)(colon - line);
	if (name_len == 14 && strncasecmp(line, "Content-Length", 14) == 0) {
		enum http_result result = parse_length(r, value, end);
		if (result != HTTP_MORE) return result;
	} else if (name_len == 17 && strncasecmp(line, "Transfer-Encoding", 17) == 0) {
		if ((size_t)(end - value) != 7 || strncasecmp(value, "chunked", 7) != 0) return HTTP_ERR_UNSUPPORTED;
		r->chunked = true;
	}
	// Zwischenantworten (1xx) werden übersprungen; ihre Kopfzeilen gehören nicht zur eigentlichen Antwort.
	if (r->status >= 200 && cb->header != NULL && !cb->header(ctx, line, len)) return HTTP_ERR_CALLBACK;
	return HTTP_MORE;
}

static enum http_result headers_done(struct http_response *r, const struct http_callbacks *cb, void *ctx)
{
	if (r->status < 200) {
		// Nach einer 1xx-Zwischenantwort folgt eine neue Statuszeile.
		bool head = r->head_request;
		uint64_t limit = r->body_limit;
		size_t header_bytes = r->header_bytes;
		http_response_init(r, head, limit);
		r->header_bytes = header_bytes;
		return HTTP_MORE;
	}
	if (cb->headers_done != NULL && !cb->headers_done(ctx, r->status)) return HTTP_ERR_CALLBACK;
	if (r->head_request || r->status == 204 || r->status == 304) {
		r->state = S_DONE;
		return HTTP_DONE;
	}
	if (r->chunked) {
		// Transfer-Encoding hat Vorrang vor Content-Length (RFC 9112, 6.3).
		r->have_length = false;
		r->remaining = 0;
		r->state = S_CHUNK_SIZE;
		return HTTP_MORE;
	}
	if (!r->have_length) {
		r->state = S_BODY_CLOSE;
		return HTTP_MORE;
	}
	if (r->remaining > r->body_limit) return HTTP_ERR_LIMIT;
	r->state = r->remaining == 0 ? S_DONE : S_BODY_LENGTH;
	return r->state == S_DONE ? HTTP_DONE : HTTP_MORE;
}

static enum http_result parse_chunk_size(struct http_response *r, const char *line, size_t len)
{
	uint64_t size = 0;
	size_t i = 0;
	for (; i < len; i++) {
		char c = line[i];
		int digit = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10
			: c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
		if (digit < 0) break;
		if (size > (UINT64_MAX >> 4)) return HTTP_ERR_LIMIT;
		size = (size << 4) | (uint64_t)digit;
	}
	// Chunk-Erweiterungen nach ';' werden ignoriert.
	if (i == 0 || (i < len && line[i] != ';' && !is_ows(line[i]))) return HTTP_ERR_SYNTAX;
	if (size == 0) {
		r->state = S_TRAILER;
		return HTTP_MORE;
	}
	if (size > r->body_limit - r->body_bytes) return HTTP_ERR_LIMIT;
	r->remaining = size;
	r->state = S_CHUNK_DATA;
	return HTTP_MORE;
}

static enum http_result process_line(struct http_response *r, const struct http_callbacks *cb, void *ctx)
{
	size_t len = r->line_len;
	if (len > 0 && r->line[len - 1] == '\r') len--;
	r->line_len = 0;
	switch (r->state) {
	case S_STATUS:
		// Leerzeilen vor der Statuszeile sind laut RFC 9112, 2.2 zu tolerieren.
		return len == 0 ? HTTP_MORE : parse_status(r, r->line, len);
	case S_HEADER: {
		enum http_result result = parse_header(r, r->line, len, cb, ctx);
		return result == HTTP_DONE ? headers_done(r, cb, ctx) : result;
	}
	case S_CHUNK_SIZE:
		return parse_chunk_size(r, r->line, len);
	case S_CHUNK_END:
		if (len != 0) return HTTP_ERR_SYNTAX;
		r->state = S_CHUNK_SIZE;
		return HTTP_MORE;
	case S_TRAILER:
		if (len != 0) return HTTP_MORE;
		r->state = S_DONE;
		return HTTP_DONE;
	default:
		return HTTP_ERR_SYNTAX;
	}
}

static bool in_line_state(int state)
{
	return state == S_STATUS || state == S_HEADER || state == S_CHUNK_SIZE || state == S_CHUNK_END || state == S_TRAILER;
}

enum http_result http_response_feed(struct http_response *r, const uint8_t *data, size_t len, size_t *used,
	const struct http_callbacks *cb, void *ctx)
{
	size_t pos = 0;
	enum http_result result = r->state == S_DONE ? HTTP_DONE : HTTP_MORE;
	while (pos < len && result == HTTP_MORE) {
		if (in_line_state(r->state)) {
			char c = (char)data[pos++];
			bool header_line = r->state == S_STATUS || r->state == S_HEADER || r->state == S_TRAILER;
			if (header_line && ++r->header_bytes > HTTP_HEADER_BYTES_MAX) { result = HTTP_ERR_LIMIT; break; }
			// Chunk-Rahmen zählt zum Rumpflimit, damit lange Erweiterungen die Übertragung nicht unbegrenzt strecken.
			if (!header_line && ++r->body_bytes > r->body_limit) { result = HTTP_ERR_LIMIT; break; }
			if (c == '\n') { result = process_line(r, cb, ctx); continue; }
			if (r->line_len >= sizeof(r->line)) { result = HTTP_ERR_LIMIT; break; }
			r->line[r->line_len++] = c;
			continue;
		}
		size_t n = len - pos;
		if (r->state != S_BODY_CLOSE && n > r->remaining) n = (size_t)r->remaining;
		if (n > r->body_limit - r->body_bytes) { result = HTTP_ERR_LIMIT; break; }
		if (cb->body != NULL && !cb->body(ctx, data + pos, n)) { result = HTTP_ERR_CALLBACK; break; }
		pos += n;
		r->body_bytes += n;
		if (r->state == S_BODY_CLOSE) continue;
		r->remaining -= n;
		if (r->remaining == 0) {
			if (r->state == S_CHUNK_DATA) r->state = S_CHUNK_END;
			else { r->state = S_DONE; result = HTTP_DONE; }
		}
	}
	if (used != NULL) *used = pos;
	return result;
}

enum http_result http_response_eof(struct http_response *r)
{
	if (r->state == S_BODY_CLOSE || r->state == S_DONE) {
		r->state = S_DONE;
		return HTTP_DONE;
	}
	return HTTP_ERR_TRUNCATED;
}

size_t http_request_build(char *out, size_t cap, const char *host, int port, const char *target,
	const char *post_body, const char *const *extra_headers)
{
	char authority[300];
	int n = port == 80 ? snprintf(authority, sizeof(authority), "%s", host)
		: snprintf(authority, sizeof(authority), "%s:%d", host, port);
	if (n < 0 || (size_t)n >= sizeof(authority)) return 0;
	size_t used = 0;
#define APPEND(...) do { \
		int written = snprintf(out + used, cap - used, __VA_ARGS__); \
		if (written < 0 || (size_t)written >= cap - used) return 0; \
		used += (size_t)written; \
	} while (0)
	if (cap == 0) return 0;
	APPEND("%s %s HTTP/1.1\r\nHost: %s\r\nConnection: close\r\nAccept-Encoding: identity\r\n",
		post_body != NULL ? "POST" : "GET", target, authority);
	for (const char *const *header = extra_headers; header != NULL && *header != NULL; header++) {
		// Eigene Rahmung: Kopfzeilen, die Länge oder Verbindung bestimmen, setzt nur der Fetcher.
		if (strncasecmp(*header, "Connection:", 11) == 0 || strncasecmp(*header, "Content-Length:", 15) == 0
			|| strncasecmp(*header, "Transfer-Encoding:", 18) == 0 || strncasecmp(*header, "Host:", 5) == 0
			|| strncasecmp(*header, "Accept-Encoding:", 16) == 0 || strpbrk(*header, "\r\n") != NULL)
			continue;
		APPEND("%s\r\n", *header);
	}
	if (post_body != NULL)
		APPEND("Content-Type: application/x-www-form-urlencoded\r\nContent-Length: %zu\r\n\r\n%s",
			strlen(post_body), post_body);
	else
		APPEND("\r\n");
#undef APPEND
	return used;
}
