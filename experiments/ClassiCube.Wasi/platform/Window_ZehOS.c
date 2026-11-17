/* ZehOS-Fensterschicht fuer ClassiCube (BSD-3-Clause wie ClassiCube, Copyright (c) 2014 - 2024, UnknownShadow200).
   Abgeleitet von src/msdos/Window_MSDOS.c. Bild, Eingabe und Fenstergroesse kommen ueber die zehos_v1-Imports
   des ZehOS-Wasm-Loaders (Vertrag: docs/PAKETE_ERSTELLEN.md im ZehOS-Repository). */
#include "_WindowBase.h"
#include "String_.h"
#include "Funcs.h"
#include "Bitmap.h"
#include "Options.h"
#include "Errors.h"

#include <stdio.h>

#define ZEHOS_IMPORT(name) __attribute__((import_module("zehos_v1"), import_name(name)))
/* 32-Bit-Bild 0x00RRGGBB; das oberste Byte ignoriert der Kernel, ClassiCubes ARGB-Puffer passt deshalb direkt. */
ZEHOS_IMPORT("present") void zehos_present(const cc_uint32* pixels, cc_int32 width, cc_int32 height, cc_int32 tic, cc_int32 test);
ZEHOS_IMPORT("should_exit") cc_int32 zehos_should_exit(void);
/* Schreibt type, code, x, y als vier i32 und liefert 1, ohne Ereignis 0. */
ZEHOS_IMPORT("poll_event") cc_int32 zehos_poll_event(cc_int32* event);
ZEHOS_IMPORT("pointer_capture") cc_int32 zehos_pointer_capture(cc_int32 enabled);

enum {
	EVENT_KEY_DOWN = 1, EVENT_KEY_UP = 2, EVENT_TEXT = 3, EVENT_POINTER_MOVE = 4, EVENT_POINTER_DOWN = 5,
	EVENT_POINTER_UP = 6, EVENT_WHEEL = 7, EVENT_RESET = 8, EVENT_RELATIVE_MOVE = 9, EVENT_RESIZE = 10
};

/* Ein Paketfenster oeffnet mit 798x519 Inhalt; Typ 10 meldet danach die tatsaechliche Groesse. */
#define DEFAULT_WIDTH  798
#define DEFAULT_HEIGHT 519
#define MAX_WIDTH  1920
#define MAX_HEIGHT 1200

static cc_int32 frameTic;
#ifdef ZEHOS_RASTER_TOGGLE
void ZehOS_RasterToggle(void);
#endif
/* ClassiCube will die Maus fangen; der Kernel lehnt ab, solange das Fenster nicht aktiv ist (etwa beim Spielstart),
   und gibt sie bei Esc und Fokusverlust selbst frei. Dann faengt der naechste Klick ins Bild sie wieder ein. */
static cc_bool wantRawMouse;

static void TryCapturePointer(void) {
	Input.RawMode = zehos_pointer_capture(1) == 0;
}


/*########################################################################################################################*
*------------------------------------------------------Mouse support------------------------------------------------------*
*#########################################################################################################################*/
/* Der Kernel liefert Positionen bereits in Bildkoordinaten; eine eigene Cursorposition gibt es nicht. */
static void Cursor_GetRawPos(int* x, int* y) { *x = 0; *y = 0; }
void Cursor_SetPosition(int x, int y) { }
static void Cursor_DoSetVisible(cc_bool visible) { }

static int MapButton(int code) {
	switch (code) {
	case 1: return CCMOUSE_L;
	case 2: return CCMOUSE_R;
	case 4: return CCMOUSE_M;
	}
	return 0;
}


/*########################################################################################################################*
*----------------------------------------------------Keyboard support-----------------------------------------------------*
*#########################################################################################################################*/
/* Normalisierte ZehOS-Tastencodes (PS/2 Set 1 ohne E0-Merkmal); Ziffernblock und Navigation teilen sich Codes. */
static const cc_uint8 key_map[] = {
/* 0x00 */ 0, CCKEY_ESCAPE, '1', '2',  '3', '4', '5', '6',
/* 0x08 */ '7', '8', '9', '0',  CCKEY_MINUS, CCKEY_EQUALS, CCKEY_BACKSPACE, CCKEY_TAB,
/* 0x10 */ 'Q', 'W', 'E', 'R',  'T', 'Y', 'U', 'I',
/* 0x18 */ 'O', 'P', CCKEY_LBRACKET, CCKEY_RBRACKET,  CCKEY_ENTER, CCKEY_LCTRL, 'A', 'S',
/* 0x20 */ 'D', 'F', 'G', 'H',  'J', 'K', 'L', CCKEY_SEMICOLON,
/* 0x28 */ CCKEY_QUOTE, CCKEY_TILDE, CCKEY_LSHIFT, CCKEY_BACKSLASH,  'Z', 'X', 'C', 'V',
/* 0x30 */ 'B', 'N', 'M', CCKEY_COMMA,  CCKEY_PERIOD, CCKEY_SLASH, CCKEY_RSHIFT, CCKEY_KP_MULTIPLY,
/* 0x38 */ CCKEY_LALT, CCKEY_SPACE, CCKEY_CAPSLOCK, CCKEY_F1,  CCKEY_F2, CCKEY_F3, CCKEY_F4, CCKEY_F5,
/* 0x40 */ CCKEY_F6, CCKEY_F7, CCKEY_F8, CCKEY_F9,  CCKEY_F10, CCKEY_NUMLOCK, CCKEY_SCROLLLOCK, CCKEY_HOME,
/* 0x48 */ CCKEY_UP, CCKEY_PAGEUP, CCKEY_KP_MINUS, CCKEY_LEFT,  CCKEY_KP5, CCKEY_RIGHT, CCKEY_KP_PLUS, CCKEY_END,
/* 0x50 */ CCKEY_DOWN, CCKEY_PAGEDOWN, CCKEY_INSERT, CCKEY_DELETE,  0, 0, 0, CCKEY_F11,
/* 0x58 */ CCKEY_F12, 0, 0, 0,  0, 0, 0, 0,
/* 0x60 */ CCKEY_RALT,
};
static int MapKey(int code) { return code >= 0 && code < (int)Array_Elems(key_map) ? key_map[code] : 0; }


/*########################################################################################################################*
*--------------------------------------------------Public implementation--------------------------------------------------*
*#########################################################################################################################*/
void Window_PreInit(void) { }

void Window_Init(void) {
	DisplayInfo.Width  = MAX_WIDTH;
	DisplayInfo.Height = MAX_HEIGHT;
	DisplayInfo.ScaleX = 1.0f;
	DisplayInfo.ScaleY = 1.0f;
}

void Window_Free(void) { }

static void DoCreateWindow(int width, int height) {
	/* Launcher und Spiel legen nacheinander "ihr" Fenster an; eine bereits gemeldete Groesse (Typ 10) bleibt. */
	if (!Window_Main.Width) {
		Window_Main.Width  = DEFAULT_WIDTH;
		Window_Main.Height = DEFAULT_HEIGHT;
	}
	Window_Main.Focused  = true;

	Window_Main.Exists   = true;
	Window_Main.UIScaleX = DEFAULT_UI_SCALE_X;
	Window_Main.UIScaleY = DEFAULT_UI_SCALE_Y;
}

void Window_Create2D(int width, int height) { DoCreateWindow(width, height); }
void Window_Create3D(int width, int height) { DoCreateWindow(width, height); }

void Window_Destroy(void) { }

void Window_SetTitle(const cc_string* title) { }

void Clipboard_GetText(cc_string* value) { }

void Clipboard_SetText(const cc_string* value) { }

int Window_GetWindowState(void) { return WINDOW_STATE_NORMAL; }

cc_result Window_EnterFullscreen(void) { return 0; }

cc_result Window_ExitFullscreen(void) { return 0; }

int Window_IsObscured(void) { return 0; }

void Window_Show(void) { }

void Window_SetSize(int width, int height) { }

/* "Quit game" im Pausenmenue; ohne Exists = false startete main das Spiel erneut. */
void Window_RequestClose(void) {
	Window_Main.Exists = false;
	Event_RaiseVoid(&WindowEvents.Closing);
}

/* Die Rasterkosten wachsen mit der Pixelzahl. Groessere Fenster zeichnet ClassiCube deshalb in einem ganzzahligen
   Bruchteil (hoechstens RENDER_MAX), den der Kernel scharf hochskaliert; Mauskoordinaten kommen in Bildkoordinaten. */
#define RENDER_MAX_WIDTH  960
#define RENDER_MAX_HEIGHT 600

static void OnResize(int width, int height) {
	int divisor;
	if (width <= 0 || height <= 0) return;
	for (divisor = 1; width / divisor > RENDER_MAX_WIDTH || height / divisor > RENDER_MAX_HEIGHT; divisor++) { }
	width /= divisor;
	height /= divisor;
	if (width == Window_Main.Width && height == Window_Main.Height) return;
	Window_Main.Width  = width;
	Window_Main.Height = height;
	Event_RaiseVoid(&WindowEvents.Resized);
}

/* Fokusverlust: Der Kernel hat den Zeiger freigegeben und Tasten losgelassen. Solange Focused false ist, oeffnet
   ClassiCube je Bild das Pausenmenue (Game.c, Option autoPause). Einen Fokusgewinn meldet der Kernel nicht; das
   naechste Tasten- oder Klickereignis kommt erst im wieder aktiven Fenster und setzt Focused zurueck. */
static void SetFocused(cc_bool focused) {
	if (Window_Main.Focused == focused) return;
	Window_Main.Focused = focused;
	Event_RaiseVoid(&WindowEvents.FocusChanged);
}

/* Reset kommt auch beim Oeffnen des Fensters; als Fokusverlust zaehlt er nur bei gefangenem Zeiger (im Spiel). */
static void OnInputReset(void) {
	Input_Clear();
	if (!Input.RawMode) return;
	Input.RawMode = false;
	SetFocused(false);
}

void Window_ProcessEvents(float delta) {
	cc_int32 event[4];
	int key;

	if (zehos_should_exit()) {
		Window_Main.Exists = false;
		Event_RaiseVoid(&WindowEvents.Closing);
		return;
	}

	while (zehos_poll_event(event) == 1) {
		switch (event[0]) {
		case EVENT_KEY_DOWN:
			SetFocused(true);
#ifdef ZEHOS_RASTER_TOGGLE
			if (event[1] == 66) { ZehOS_RasterToggle(); break; } /* F8, nur in Testbuilds */
#endif
			if ((key = MapKey(event[1]))) Input_SetPressed(key);
			break;
		case EVENT_KEY_UP:
			if ((key = MapKey(event[1]))) Input_SetReleased(key);
			break;
		case EVENT_TEXT:
			Event_RaiseInt(&InputEvents.Press, event[1]);
			break;
		case EVENT_POINTER_MOVE:
			/* Absolute Bewegung heisst: Der Kernel hat den Fang aufgehoben. */
			Input.RawMode = false;
			Pointer_SetPosition(0, event[2], event[3]);
			break;
		case EVENT_POINTER_DOWN:
			SetFocused(true);
			/* Der Klick, der die Maus einfaengt, loest im Spiel nichts aus (wie bei Doom). */
			if (wantRawMouse && !Input.RawMode) { TryCapturePointer(); break; }
			/* fallthrough */
		case EVENT_POINTER_UP:
			Pointer_SetPosition(0, event[2], event[3]);
			if ((key = MapButton(event[1]))) Input_Set(key, event[0] == EVENT_POINTER_DOWN);
			break;
		case EVENT_WHEEL:
			Mouse_ScrollVWheel((float)event[1]);
			break;
		case EVENT_RESET:
			OnInputReset();
			break;
		case EVENT_RELATIVE_MOVE:
			/* Typ 9 kommt nur bei gefangenem Zeiger; eine vorher eingereihte absolute Bewegung kann RawMode
			   faelschlich geloescht haben. */
			if (wantRawMouse) Input.RawMode = true;
			if (Input.RawMode) Event_RaiseRawMove(&PointerEvents.RawMoved, (float)event[2], (float)event[3]);
			break;
		case EVENT_RESIZE:
			OnResize(event[2], event[3]);
			break;
		}
	}
}

void Gamepads_PreInit(void) { }

void Gamepads_Init(void) { }

void Gamepads_Process(float delta) { }

/* stderr erscheint bei Exitcode ungleich 0 mit der letzten Zeile im Fenster. */
static void ShowDialogCore(const char* title, const char* msg) {
	fprintf(stderr, "%s: %s\n", title, msg);
}

cc_result Window_OpenFileDialog(const struct OpenFileDialogArgs* args) {
	return ERR_NOT_SUPPORTED;
}

cc_result Window_SaveFileDialog(const struct SaveFileDialogArgs* args) {
	return ERR_NOT_SUPPORTED;
}

void Window_AllocFramebuffer(struct Bitmap* bmp, int width, int height) {
	bmp->scan0  = (BitmapCol*)Mem_Alloc(width * height, BITMAPCOLOR_SIZE, "window pixels");
	bmp->width  = width;
	bmp->height = height;
}

/* Der Kernel uebertraegt immer das ganze Bild; Teilbereiche (r) gibt es in der Schnittstelle nicht. */
void Window_DrawFramebuffer(Rect2D r, struct Bitmap* bmp) {
	zehos_present((const cc_uint32*)bmp->scan0, bmp->width, bmp->height, ++frameTic, 0);
}

void Window_FreeFramebuffer(struct Bitmap* bmp) {
	Mem_Free(bmp->scan0);
}

void OnscreenKeyboard_Open(struct OpenKeyboardArgs* args) { }
void OnscreenKeyboard_SetText(const cc_string* text) { }
void OnscreenKeyboard_Close(void) { }

void Window_EnableRawMouse(void) {
	wantRawMouse = true;
	TryCapturePointer();
}

void Window_UpdateRawMouse(void) { }

void Window_DisableRawMouse(void) {
	wantRawMouse = false;
	zehos_pointer_capture(0);
	Input.RawMode = false;
}
