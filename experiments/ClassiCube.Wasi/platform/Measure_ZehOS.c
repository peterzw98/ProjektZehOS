/* Zeitmessung fuer den Entscheidungspunkt nach Etappe 2 (BSD-3-Clause wie ClassiCube).
   Der Linker leitet ausgewaehlte ClassiCube-Funktionen per --wrap hierher um (build.py), der ClassiCube-Kern bleibt
   unveraendert. Der Kernel verwirft stdout von Paket-Apps; die Werte erscheinen deshalb im Chat des Spiels: alle 5 s
   eine Zeile und die Weltgenerierung einmal. Gemessen wird mit der monotonen WASI-Uhr. */
#include "Core.h"
#include "Chat.h"
#include "Platform.h"
#include "String_.h"
#include "Builder.h"
#include "Generator.h"
#include "Graphics.h"

#include <stdio.h>

#define REPORT_INTERVAL_US 5000000ULL

void __real_Gfx_DrawVb_IndexedTris_Range(int verticesCount, int startVertex, DrawHints hints);
void __real_Gfx_DrawVb_IndexedTris(int verticesCount);
void __real_Gfx_DrawIndexedTris_T2fC4b(int verticesCount, int startVertex, DrawHints hints);
void __real_Gfx_ClearBuffers(GfxBuffers buffers);
void __real_Gfx_EndFrame(void);
cc_bool __real_Builder_MakeChunk(struct ChunkInfo* info);
void __real_Gen_Start(const struct MapGenerator* gen, int seed, int width, int height, int length);
cc_bool __real_Gen_IsDone(void);

/* Summen im laufenden Berichtsfenster, in Mikrosekunden. */
static cc_uint64 rasterUs, meshUs, presentUs, windowStart;
static int frames;
/* Weltgenerierung: Beginn (Wanduhr) und reine Rechenzeit der Generatorschritte. */
static cc_uint64 genBegin, genComputeUs;
static cc_bool genRunning;

static cc_uint64 Elapsed(cc_uint64 begin) { return Stopwatch_ElapsedMicroseconds(begin, Stopwatch_Measure()); }

static void Report(const char* text, int msgType) {
	cc_string str = String_FromReadonly(text);
	Platform_LogConst(text);
	Chat_AddOf(&str, msgType);
}

void __wrap_Gfx_DrawVb_IndexedTris_Range(int verticesCount, int startVertex, DrawHints hints) {
	cc_uint64 begin = Stopwatch_Measure();
	__real_Gfx_DrawVb_IndexedTris_Range(verticesCount, startVertex, hints);
	rasterUs += Elapsed(begin);
}

void __wrap_Gfx_DrawVb_IndexedTris(int verticesCount) {
	cc_uint64 begin = Stopwatch_Measure();
	__real_Gfx_DrawVb_IndexedTris(verticesCount);
	rasterUs += Elapsed(begin);
}

void __wrap_Gfx_DrawIndexedTris_T2fC4b(int verticesCount, int startVertex, DrawHints hints) {
	cc_uint64 begin = Stopwatch_Measure();
	__real_Gfx_DrawIndexedTris_T2fC4b(verticesCount, startVertex, hints);
	rasterUs += Elapsed(begin);
}

/* Loeschen von Farb- und Tiefenpuffer zaehlt zur Rasterisierung, weil es mit der Bildgroesse waechst. */
void __wrap_Gfx_ClearBuffers(GfxBuffers buffers) {
	cc_uint64 begin = Stopwatch_Measure();
	__real_Gfx_ClearBuffers(buffers);
	rasterUs += Elapsed(begin);
}

cc_bool __wrap_Builder_MakeChunk(struct ChunkInfo* info) {
	cc_uint64 begin = Stopwatch_Measure();
	cc_bool result = __real_Builder_MakeChunk(info);
	meshUs += Elapsed(begin);
	return result;
}

/* Gfx_EndFrame uebergibt das Bild (present); dort wartet der Kernel auch, solange das Fenster nicht aktiv ist. */
void __wrap_Gfx_EndFrame(void) {
	cc_uint64 begin = Stopwatch_Measure(), total, measured;
	char text[192];
	__real_Gfx_EndFrame();
	presentUs += Elapsed(begin);
	frames++;

	/* Das erste Fenster beginnt hier; vorher aufgelaufene Summen (Ladebildschirm) zaehlen nicht. */
	if (!windowStart) { windowStart = Stopwatch_Measure(); rasterUs = meshUs = presentUs = 0; frames = 0; return; }
	total = Elapsed(windowStart);
	if (total < REPORT_INTERVAL_US) return;
	measured = rasterUs + meshUs + presentUs;

	/* Werte je Bild in Millisekunden; "Rest" ist Spiellogik, Eingabe, Physik, Oberflaeche und Verwaltung. */
	snprintf(text, sizeof(text), "&e%d.%d fps, je Bild: Raster %d, Mesh %d, Present %d, Rest %d ms",
		(int)(frames * 10000000ULL / total) / 10, (int)(frames * 10000000ULL / total) % 10,
		(int)(rasterUs / 1000 / frames), (int)(meshUs / 1000 / frames), (int)(presentUs / 1000 / frames),
		(int)((total > measured ? total - measured : 0) / 1000 / frames));
	/* Im Chat statt in einer Statuszeile: Die Statuszeilen oben rechts erschienen im Gast nicht (Ursache nicht geklaert). */
	Report(text, MSG_TYPE_NORMAL);
	rasterUs = meshUs = presentUs = 0;
	frames = 0;
	windowStart = Stopwatch_Measure();
}

void __wrap_Gen_Start(const struct MapGenerator* gen, int seed, int width, int height, int length) {
	cc_uint64 begin = Stopwatch_Measure();
	genBegin = begin;
	genComputeUs = 0;
	genRunning = true;
	__real_Gen_Start(gen, seed, width, height, length);
	genComputeUs += Elapsed(begin);
}

/* Im kooperativen Betrieb fuehrt Gen_IsDone die naechsten Generatorschritte aus. */
cc_bool __wrap_Gen_IsDone(void) {
	cc_uint64 begin = Stopwatch_Measure();
	cc_bool done = __real_Gen_IsDone();
	char text[128];
	if (!genRunning) return done;

	genComputeUs += Elapsed(begin);
	if (!done) return done;
	genRunning = false;
	snprintf(text, sizeof(text), "&eWelt erzeugt in %d ms (Rechenzeit Generator %d ms)",
		(int)(Elapsed(genBegin) / 1000), (int)(genComputeUs / 1000));
	Report(text, MSG_TYPE_NORMAL);
	return done;
}
