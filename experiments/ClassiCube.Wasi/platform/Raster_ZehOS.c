/* Rasterisierung als ZehOS-Hostfunktion (BSD-3-Clause wie ClassiCube). Patch 0003 ruft diese Funktionen aus
   Graphics_SoftGPU.c; liefern sie einen Fehler, zeichnet SoftGPU im Gast. Vertrag: zehos_v1.raster_draw und
   raster_fill, Erkennung ueber ZEHOS_FEATURES (docs/PAKETE_ERSTELLEN.md im ZehOS-Repository). */
#include "Core.h"

#include <stdlib.h>
#include <string.h>

#define ZEHOS_IMPORT(name) __attribute__((import_module("zehos_v1"), import_name(name)))
ZEHOS_IMPORT("raster_draw") cc_int32 zehos_raster_draw(const cc_uint32* call);
ZEHOS_IMPORT("raster_fill") cc_int32 zehos_raster_fill(void* data, cc_uint32 count, cc_uint32 value);

/* -1 noch nicht geprueft, 0 nicht vorhanden, 1 vorhanden. */
static int rasterAvailable = -1;

/* Sucht name als ganzen Eintrag in der kommagetrennten Liste. */
static int HasFeature(const char* list, const char* name) {
	size_t length = strlen(name);
	while (list && *list) {
		const char* end = strchr(list, ',');
		size_t entry = end ? (size_t)(end - list) : strlen(list);
		if (entry == length && memcmp(list, name, length) == 0) return 1;
		list = end ? end + 1 : NULL;
	}
	return 0;
}

static int RasterAvailable(void) {
#ifdef ZEHOS_RASTER_SOFT
	return 0;
#else
	if (rasterAvailable < 0) rasterAvailable = HasFeature(getenv("ZEHOS_FEATURES"), "raster_v1");
	return rasterAvailable;
#endif
}

/* wasm3 uebersetzt eine Funktion erst beim ersten Aufruf. Aeltere Kernel ohne raster_* trappen deshalb nur, wenn
   eine Funktion mit dem Import uebersetzt wird; noinline haelt die Importaufrufe aus den Aufrufern heraus. */
__attribute__((noinline)) static int CallDraw(const cc_uint32* call) { return zehos_raster_draw(call); }
__attribute__((noinline)) static int CallFill(void* data, cc_uint32 count, cc_uint32 value) {
	return zehos_raster_fill(data, count, value);
}

/* Nur in Testbuilds (build.py --raster-toggle): F8 schaltet zwischen Kernel und SoftGPU um, damit ein Lauf beide
   Bilder derselben Szene liefert. */
static cc_bool rasterPaused;

void ZehOS_RasterToggle(void) { rasterPaused = !rasterPaused; }

int ZehOS_RasterDraw(const cc_uint32* call) {
	return !rasterPaused && RasterAvailable() ? CallDraw(call) : -1;
}

int ZehOS_RasterFill(void* data, cc_uint32 count, cc_uint32 value) {
	return !rasterPaused && RasterAvailable() ? CallFill(data, count, value) : -1;
}
