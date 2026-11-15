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
void zehos_present(const uint32_t *pixels, int32_t width, int32_t height, int32_t tic, int32_t test) { }
int32_t zehos_poll_event(int32_t *event) { return 0; }
uint32_t zehos_ticks_ms(void) { return 0; }
void zehos_sleep_ms(uint32_t ms) { }
int32_t zehos_should_exit(void) { return 0; }

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
    puts("NetSurf input PASS");
    return 0;
}
