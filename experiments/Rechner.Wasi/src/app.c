// Rechner als ZehOS-Paket-App: zeichnet ueber zehos_v1.present und liest Maus/Tastatur ueber poll_event.
// SPDX-License-Identifier: GPL-3.0-or-later

#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include "calc.h"

#define ZEHOS_IMPORT(name) __attribute__((import_module("zehos_v1"), import_name(name)))

ZEHOS_IMPORT("present") void zehos_present(const uint32_t* pixels, int32_t width, int32_t height, int32_t tic, int32_t test);
ZEHOS_IMPORT("sleep_ms") void zehos_sleep_ms(uint32_t milliseconds);
ZEHOS_IMPORT("should_exit") int32_t zehos_should_exit(void);
// Schreibt type, code, x, y als vier i32; 1 bei einem Ereignis, 0 ohne.
ZEHOS_IMPORT("poll_event") int32_t zehos_poll_event(int32_t* event);

enum { EVENT_KEY_DOWN = 1, EVENT_TEXT = 3, EVENT_POINTER_DOWN = 5, EVENT_POINTER_UP = 6, EVENT_RESET = 8 };
enum { KEY_ESC = 1, KEY_BACKSPACE = 14, KEY_ENTER = 28, KEY_DELETE = 83 };

// Ein Paketfenster oeffnet mit genau diesem Inhalt; so bleibt das Bild 1:1 scharf.
#define WIDTH 798
#define HEIGHT 519

static uint32_t frame[WIDTH * HEIGHT];
static calc_state state;

enum
{
    BACKGROUND = 0x1e1e2e, PANEL = 0x181825, TEXT = 0xcdd6f4, MUTED = 0x7f849c, ERROR = 0xf38ba8,
    DIGIT = 0x313244, OPERATOR = 0x45475a, FUNCTION = 0x585b70, EQUALS = 0x89b4fa, EQUALS_TEXT = 0x11111b
};

// 5x7-Glyphen, je Zeile 5 Bit (Bit 4 links). '*' und '/' erscheinen als Mal- und Geteiltzeichen, '|' als Schraegstrich.
typedef struct { char c; uint8_t rows[7]; } glyph;

static const glyph font[] = {
    { '0', { 0x0e, 0x11, 0x13, 0x15, 0x19, 0x11, 0x0e } }, { '1', { 0x04, 0x0c, 0x04, 0x04, 0x04, 0x04, 0x0e } },
    { '2', { 0x0e, 0x11, 0x01, 0x02, 0x04, 0x08, 0x1f } }, { '3', { 0x1f, 0x02, 0x04, 0x02, 0x01, 0x11, 0x0e } },
    { '4', { 0x02, 0x06, 0x0a, 0x12, 0x1f, 0x02, 0x02 } }, { '5', { 0x1f, 0x10, 0x1e, 0x01, 0x01, 0x11, 0x0e } },
    { '6', { 0x06, 0x08, 0x10, 0x1e, 0x11, 0x11, 0x0e } }, { '7', { 0x1f, 0x01, 0x02, 0x04, 0x08, 0x08, 0x08 } },
    { '8', { 0x0e, 0x11, 0x11, 0x0e, 0x11, 0x11, 0x0e } }, { '9', { 0x0e, 0x11, 0x11, 0x0f, 0x01, 0x02, 0x0c } },
    { '+', { 0x00, 0x04, 0x04, 0x1f, 0x04, 0x04, 0x00 } }, { '-', { 0x00, 0x00, 0x00, 0x1f, 0x00, 0x00, 0x00 } },
    { '*', { 0x00, 0x11, 0x0a, 0x04, 0x0a, 0x11, 0x00 } }, { '/', { 0x00, 0x04, 0x00, 0x1f, 0x00, 0x04, 0x00 } },
    { '|', { 0x01, 0x02, 0x02, 0x04, 0x08, 0x08, 0x10 } }, { '%', { 0x18, 0x19, 0x02, 0x04, 0x08, 0x13, 0x03 } },
    { '(', { 0x02, 0x04, 0x08, 0x08, 0x08, 0x04, 0x02 } }, { ')', { 0x08, 0x04, 0x02, 0x02, 0x02, 0x04, 0x08 } },
    { '.', { 0x00, 0x00, 0x00, 0x00, 0x00, 0x0c, 0x0c } }, { '=', { 0x00, 0x00, 0x1f, 0x00, 0x1f, 0x00, 0x00 } },
    { 'e', { 0x00, 0x00, 0x0e, 0x11, 0x1f, 0x10, 0x0e } }, { '<', { 0x02, 0x04, 0x08, 0x10, 0x08, 0x04, 0x02 } },
    { 'A', { 0x0e, 0x11, 0x11, 0x1f, 0x11, 0x11, 0x11 } }, { 'B', { 0x1e, 0x11, 0x11, 0x1e, 0x11, 0x11, 0x1e } },
    { 'C', { 0x0e, 0x11, 0x10, 0x10, 0x10, 0x11, 0x0e } }, { 'D', { 0x1c, 0x12, 0x11, 0x11, 0x11, 0x12, 0x1c } },
    { 'E', { 0x1f, 0x10, 0x10, 0x1e, 0x10, 0x10, 0x1f } }, { 'F', { 0x1f, 0x10, 0x10, 0x1e, 0x10, 0x10, 0x10 } },
    { 'G', { 0x0e, 0x11, 0x10, 0x17, 0x11, 0x11, 0x0f } }, { 'H', { 0x11, 0x11, 0x11, 0x1f, 0x11, 0x11, 0x11 } },
    { 'I', { 0x0e, 0x04, 0x04, 0x04, 0x04, 0x04, 0x0e } }, { 'J', { 0x07, 0x02, 0x02, 0x02, 0x02, 0x12, 0x0c } },
    { 'K', { 0x11, 0x12, 0x14, 0x18, 0x14, 0x12, 0x11 } }, { 'L', { 0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x1f } },
    { 'M', { 0x11, 0x1b, 0x15, 0x15, 0x11, 0x11, 0x11 } }, { 'N', { 0x11, 0x11, 0x19, 0x15, 0x13, 0x11, 0x11 } },
    { 'O', { 0x0e, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0e } }, { 'P', { 0x1e, 0x11, 0x11, 0x1e, 0x10, 0x10, 0x10 } },
    { 'Q', { 0x0e, 0x11, 0x11, 0x11, 0x15, 0x12, 0x0d } }, { 'R', { 0x1e, 0x11, 0x11, 0x1e, 0x14, 0x12, 0x11 } },
    { 'S', { 0x0f, 0x10, 0x10, 0x0e, 0x01, 0x01, 0x1e } }, { 'T', { 0x1f, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04 } },
    { 'U', { 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0e } }, { 'V', { 0x11, 0x11, 0x11, 0x11, 0x11, 0x0a, 0x04 } },
    { 'W', { 0x11, 0x11, 0x11, 0x15, 0x15, 0x15, 0x0a } }, { 'X', { 0x11, 0x11, 0x0a, 0x04, 0x0a, 0x11, 0x11 } },
    { 'Y', { 0x11, 0x11, 0x0a, 0x04, 0x04, 0x04, 0x04 } }, { 'Z', { 0x1f, 0x01, 0x02, 0x04, 0x08, 0x10, 0x1f } },
};

static const glyph* find_glyph(char c)
{
    for (unsigned i = 0; i < sizeof(font) / sizeof(font[0]); i++)
        if (font[i].c == c) return &font[i];
    return NULL;
}

static void fill(int x, int y, int w, int h, uint32_t color)
{
    for (int row = y < 0 ? 0 : y; row < y + h && row < HEIGHT; row++)
        for (int col = x < 0 ? 0 : x; col < x + w && col < WIDTH; col++)
            frame[row * WIDTH + col] = color;
}

static int text_width(const char* text, int scale) { int n = (int)strlen(text); return n == 0 ? 0 : n * 6 * scale - scale; }

static void draw_text(const char* text, int x, int y, int scale, uint32_t color)
{
    for (; *text; text++, x += 6 * scale)
    {
        const glyph* g = find_glyph(*text);
        if (g == NULL) continue;
        for (int row = 0; row < 7; row++)
            for (int col = 0; col < 5; col++)
                if (g->rows[row] & (0x10 >> col)) fill(x + col * scale, y + row * scale, scale, scale, color);
    }
}

// Rechtsbuendig; zu lange Texte zeigen ihr Ende, das die zuletzt getippten Zeichen enthaelt.
static void draw_right(const char* text, int right, int y, int scale, int max_width, uint32_t color)
{
    int max_chars = (max_width + scale) / (6 * scale);
    int length = (int)strlen(text);
    const char* shown = length > max_chars ? text + length - max_chars : text;
    draw_text(shown, right - text_width(shown, scale), y, scale, color);
}

enum { ACT_CHAR, ACT_CLEAR, ACT_CLEAR_ENTRY, ACT_BACKSPACE, ACT_NEGATE, ACT_EVALUATE };

typedef struct { int row, col, rows, cols; const char* label; int action; char c; uint32_t color; } button;

static const button buttons[] = {
    { 0, 0, 1, 1, "C", ACT_CLEAR, 0, FUNCTION }, { 0, 1, 1, 1, "CE", ACT_CLEAR_ENTRY, 0, FUNCTION },
    { 0, 2, 1, 1, "<", ACT_BACKSPACE, 0, FUNCTION }, { 0, 3, 1, 1, "(", ACT_CHAR, '(', OPERATOR },
    { 0, 4, 1, 1, ")", ACT_CHAR, ')', OPERATOR },
    { 1, 0, 1, 1, "7", ACT_CHAR, '7', DIGIT }, { 1, 1, 1, 1, "8", ACT_CHAR, '8', DIGIT },
    { 1, 2, 1, 1, "9", ACT_CHAR, '9', DIGIT }, { 1, 3, 1, 1, "/", ACT_CHAR, '/', OPERATOR },
    { 1, 4, 1, 1, "%", ACT_CHAR, '%', OPERATOR },
    { 2, 0, 1, 1, "4", ACT_CHAR, '4', DIGIT }, { 2, 1, 1, 1, "5", ACT_CHAR, '5', DIGIT },
    { 2, 2, 1, 1, "6", ACT_CHAR, '6', DIGIT }, { 2, 3, 1, 1, "*", ACT_CHAR, '*', OPERATOR },
    { 2, 4, 1, 1, "+|-", ACT_NEGATE, 0, OPERATOR },
    { 3, 0, 1, 1, "1", ACT_CHAR, '1', DIGIT }, { 3, 1, 1, 1, "2", ACT_CHAR, '2', DIGIT },
    { 3, 2, 1, 1, "3", ACT_CHAR, '3', DIGIT }, { 3, 3, 1, 1, "-", ACT_CHAR, '-', OPERATOR },
    { 3, 4, 2, 1, "=", ACT_EVALUATE, 0, EQUALS },
    { 4, 0, 1, 2, "0", ACT_CHAR, '0', DIGIT }, { 4, 2, 1, 1, ".", ACT_CHAR, '.', DIGIT },
    { 4, 3, 1, 1, "+", ACT_CHAR, '+', OPERATOR },
};
#define BUTTON_COUNT ((int)(sizeof(buttons) / sizeof(buttons[0])))

enum { MARGIN = 16, GAP = 8, PANEL_HEIGHT = 184, GRID_TOP = MARGIN + PANEL_HEIGHT + 12 };
#define CELL_W ((WIDTH - 2 * MARGIN - 4 * GAP) / 5)
#define CELL_H ((HEIGHT - GRID_TOP - MARGIN - 4 * GAP) / 5)

static void bounds(const button* b, int* x, int* y, int* w, int* h)
{
    *x = MARGIN + b->col * (CELL_W + GAP);
    *y = GRID_TOP + b->row * (CELL_H + GAP);
    *w = b->cols * CELL_W + (b->cols - 1) * GAP;
    *h = b->rows * CELL_H + (b->rows - 1) * GAP;
}

static int hit(int px, int py)
{
    for (int i = 0; i < BUTTON_COUNT; i++)
    {
        int x, y, w, h;
        bounds(&buttons[i], &x, &y, &w, &h);
        if (px >= x && px < x + w && py >= y && py < y + h) return i;
    }
    return -1;
}

static int pressed = -1;

static uint32_t lighter(uint32_t color)
{
    uint32_t r = (color >> 16) & 0xff, g = (color >> 8) & 0xff, b = color & 0xff;
    r += (255 - r) / 4; g += (255 - g) / 4; b += (255 - b) / 4;
    return (r << 16) | (g << 8) | b;
}

static void render(void)
{
    fill(0, 0, WIDTH, HEIGHT, BACKGROUND);
    fill(MARGIN, MARGIN, WIDTH - 2 * MARGIN, PANEL_HEIGHT, PANEL);
    int right = WIDTH - 2 * MARGIN, inner = WIDTH - 4 * MARGIN;
    for (int i = 0; i < state.history_count; i++)
        draw_right(state.history[i], right, MARGIN + 10 + i * 20, 2, inner, MUTED);
    char line[CALC_MAX_INPUT + 2];
    if (state.evaluated)
    {
        // Nach "=" steht die Rechnung klein darueber, das Ergebnis gross darunter.
        snprintf(line, sizeof(line), "%s=", state.input);
        draw_right(line, right, MARGIN + 96, 3, inner, MUTED);
        draw_right(state.result, right, MARGIN + 132, 6, inner, state.error ? ERROR : TEXT);
    }
    else
        draw_right(state.input[0] ? state.input : "0", right, MARGIN + 132, 6, inner, TEXT);
    for (int i = 0; i < BUTTON_COUNT; i++)
    {
        const button* b = &buttons[i];
        int x, y, w, h;
        bounds(b, &x, &y, &w, &h);
        fill(x, y, w, h, i == pressed ? lighter(b->color) : b->color);
        int scale = 4;
        draw_text(b->label, x + (w - text_width(b->label, scale)) / 2, y + (h - 7 * scale) / 2, scale,
                  b->color == EQUALS ? EQUALS_TEXT : TEXT);
    }
    zehos_present(frame, WIDTH, HEIGHT, 0, 0);
}

static void activate(const button* b)
{
    switch (b->action)
    {
        case ACT_CHAR: calc_input(&state, b->c); break;
        case ACT_CLEAR: calc_clear(&state); break;
        case ACT_CLEAR_ENTRY: calc_clear_entry(&state); break;
        case ACT_BACKSPACE: calc_backspace(&state); break;
        case ACT_NEGATE: calc_negate(&state); break;
        default: calc_evaluate(&state); break;
    }
}

// Liefert 1, wenn sich die Anzeige geaendert haben kann.
static int handle(const int32_t* event)
{
    switch (event[0])
    {
        case EVENT_POINTER_DOWN:
            if (event[1] != 1) return 0;
            pressed = hit(event[2], event[3]);
            return pressed >= 0;
        case EVENT_POINTER_UP:
            if (event[1] != 1 || pressed < 0) return 0;
            // Nur ein Loslassen auf derselben Taste loest sie aus, wie bei ueblichen Schaltflaechen.
            if (hit(event[2], event[3]) == pressed) activate(&buttons[pressed]);
            pressed = -1;
            return 1;
        case EVENT_RESET:
            pressed = -1;
            return 1;
        case EVENT_KEY_DOWN:
            switch (event[1])
            {
                case KEY_ENTER: calc_evaluate(&state); return 1;
                case KEY_BACKSPACE: calc_backspace(&state); return 1;
                case KEY_DELETE: calc_clear_entry(&state); return 1;
                case KEY_ESC: calc_clear(&state); return 1;
                default: return 0;
            }
        case EVENT_TEXT:
        {
            int32_t c = event[1];
            if (c == '=') { calc_evaluate(&state); return 1; }
            if (c == 'c' || c == 'C') { calc_clear(&state); return 1; }
            // Steuerzeichen von Enter/Ruecktaste kommen schon als Taste; alles ausser ASCII verwirft calc_input.
            if (c < 32 || c > 126) return 0;
            return calc_input(&state, (char)c);
        }
        default:
            return 0;
    }
}

int main(void)
{
    calc_reset(&state);
    render();
    int32_t event[4];
    while (!zehos_should_exit())
    {
        int changed = 0;
        while (zehos_poll_event(event) == 1) changed |= handle(event);
        if (changed) render();
        else zehos_sleep_ms(15);
    }
    return 0;
}
