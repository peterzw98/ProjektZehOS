// Testet die echte Ereignisuebersetzung mit den Headern des gepinnten libnsfb.
#include <assert.h>
#include <stdbool.h>
#include <stdio.h>
#include "libnsfb.h"
#define NSFB_SURFACE_ZEHOS NSFB_SURFACE_RAM
#include "../frontend/zehos.c"

void _nsfb_register_surface(const enum nsfb_type_e type, const nsfb_surface_rtns_t *rtns, const char *name) { }
bool select_plotters(nsfb_t *nsfb) { return true; }
bool nsfb_cursor_plot(nsfb_t *nsfb, struct nsfb_cursor_s *cursor) { return true; }
static int presented;
void zehos_present(const uint32_t *pixels, int32_t width, int32_t height, int32_t tic, int32_t test) { presented++; }
int32_t zehos_poll_event(int32_t *event) { return 0; }
uint32_t zehos_ticks_ms(void) { return 0; }
void zehos_sleep_ms(uint32_t ms) { }
int32_t zehos_should_exit(void) { return 0; }

static bool reset_clip(nsfb_t *nsfb, nsfb_bbox_t *clip) {
    nsfb_bbox_t full = {0, 0, nsfb->width, nsfb->height};
    nsfb->clip = full;
    return clip == NULL;
}

static bool send(int type, int code, int x, int y, nsfb_event_t *event) {
    int32_t raw[4] = {type, code, x, y};
    return translate(raw, event);
}

int main(void) {
    nsfb_event_t event;
    assert(!send(EV_KEYDOWN, 16, 0, 0, &event));
    assert(send(EV_TEXT, 0x1f680, 0, 0, &event));
    assert(event.value.keycode == UCS4_KEY_BASE + 0x1f680);
    assert(send(EV_KEYDOWN, 42, 0, 0, &event));
    assert(send(EV_KEYDOWN, 42, 0, 0, &event)); // Wiederholung haelt den Modifier nur einmal.
    for (int button = 1; button <= 4; button <<= 1) {
        assert(send(EV_BUTTONDOWN, button, 27, 39, &event));
        assert(event.type == NSFB_EVENT_MOVE_ABSOLUTE);
        assert(event.value.vector.x == 27 && event.value.vector.y == 39);
        assert(take_pending(&event));
        assert(event.type == NSFB_EVENT_KEY_DOWN && event.value.keycode == map_button(button));
    }
    assert(!send(EV_RESET, 0, 0, 0, &event));
    int count = 0, released = 0;
    while (take_pending(&event)) {
        if (event.type == NSFB_EVENT_KEY_UP && event.value.keycode == NSFB_KEY_LSHIFT) released |= 1;
        if (event.type == NSFB_EVENT_CONTROL && event.value.controlcode == CONTROL_CANCEL_POINTER) released |= 2;
        count++;
    }
    assert(count == 2 && released == 3); // Maus abbrechen statt per Up einen Link anzuklicken.
    assert(!send(EV_BUTTONUP, 1, 27, 39, &event)); // Physisches Loslassen nach Reset bleibt wirkungslos.
    assert(!ctrl_held());
    assert(!send(EV_RESET, 0, 0, 0, &event));
    assert(!take_pending(&event));
    assert(send(EV_WHEEL, -1, 11, 22, &event));
    assert(event.type == NSFB_EVENT_MOVE_ABSOLUTE && event.value.vector.y == 22);
    assert(take_pending(&event) && event.type == NSFB_EVENT_KEY_DOWN && event.value.keycode == NSFB_KEY_MOUSE_5);
    assert(take_pending(&event) && event.type == NSFB_EVENT_KEY_UP);
    assert(!take_pending(&event));
    assert(send(EV_WHEEL, INT32_MIN, 11, 22, &event));
    count = 0;
    while (take_pending(&event)) count++;
    assert(count == 32);
    assert(send(EV_RESIZE, 0, 1024, 700, &event));
    assert(event.type == NSFB_EVENT_RESIZE && event.value.resize.w == 1024 && event.value.resize.h == 700);
    nsfb_plotter_fns_t plotters = { .set_clip = reset_clip };
    nsfb_t surface = { .plotter_fns = &plotters };
    assert(zehos_defaults(&surface) == 0);
    // Die Startanfrage von NetSurf (vor nsfb_init) aendert die Startgroesse nicht.
    assert(zehos_set_geometry(&surface, 800, 600, NSFB_FMT_ANY) == 0 && surface.width == ZEHOS_WIDTH);
    assert(zehos_initialise(&surface) == 0);
    surface.clip.x1 = ZEHOS_WIDTH;
    for (size_t i = 0; i < (size_t)ZEHOS_WIDTH * ZEHOS_HEIGHT * 4; i++) surface.ptr[i] = 0x5a;
    assert(zehos_set_geometry(&surface, 640, 400, NSFB_FMT_ANY) == 0);
    // Keine alten Bildpunkte mit falscher Zeilenlaenge nach dem Groessenwechsel.
    for (size_t i = 0; i < (size_t)640 * 400 * 4; i++) assert(surface.ptr[i] == 0);
    assert(surface.width == 640 && surface.height == 400 && surface.linelen == 640 * 4 && surface.clip.x1 == 640);
    assert(zehos_set_geometry(&surface, 1921, 400, NSFB_FMT_ANY) == -1 && surface.width == 640);
    assert(zehos_set_geometry(&surface, 1920, 1200, NSFB_FMT_ANY) == 0 && surface.linelen == 1920 * 4);
    for (size_t i = 0; i < (size_t)1920 * 1200 * 4; i += 4099) assert(surface.ptr[i] == 0);
    surface.ptr[(size_t)1920 * 1200 * 4 - 1] = 1; // Puffer deckt das ganze neue Bild ab (ASan/Valgrind).
    // Mehrere Widget-Updates eines Neuzeichnens ergeben genau ein Bild, gesendet vor der naechsten Eingabeabfrage.
    nsfb_bbox_t box = {0, 0, 1, 1};
    assert(zehos_update(&surface, &box) == 0 && zehos_update(&surface, &box) == 0 && presented == 0);
    assert(!zehos_input(&surface, &event, 0) && presented == 1);
    assert(!zehos_input(&surface, &event, 0) && presented == 1);
    // Regelweg: NetSurfs Hauptschleife sendet direkt nach dem Neuzeichnen.
    assert(zehos_update(&surface, &box) == 0);
    zehos_flush();
    zehos_flush();
    assert(presented == 2);
    zehos_finalise(&surface);
    puts("NetSurf input PASS");
    return 0;
}
