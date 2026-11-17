/* ZehOS-Plattformschicht fuer ClassiCube (BSD-3-Clause wie ClassiCube, Copyright (c) 2014 - 2024, UnknownShadow200).
   Abgeleitet von src/msdos/Platform_MSDOS.c: ein Faden, keine Sockets, keine Prozesse. Laeuft als WASIp1-Programm
   im ZehOS-Wasm-Loader; Dateien sind nur lesbar, Schreibversuche scheitern mit einem Fehlercode. */
#define CC_NO_UPDATER
#define CC_NO_DYNLIB
#define CC_NO_SOCKETS
#define CC_NO_THREADING
#define CC_NO_ENCRYPTION
#define CC_NO_OPEN
#define CC_NO_CRASHHANDLER

#include "Stream.h"
#include "ExtMath.h"
#include "SystemFonts.h"
#include "Funcs.h"
#include "Window.h"
#include "Utils.h"
#include "Errors.h"
#include "PackedCol.h"

#include <errno.h>
#include <time.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <dirent.h>
#include <fcntl.h>
#include <sys/stat.h>
#include <sys/time.h>
#include <stdio.h>

#define ZEHOS_IMPORT(name) __attribute__((import_module("zehos_v1"), import_name(name)))
/* WASI kennt kein poll_oneoff im Loader, usleep der libc wuerde scheitern. */
ZEHOS_IMPORT("sleep_ms") void zehos_sleep_ms(cc_uint32 milliseconds);

const cc_result ReturnCode_FileShareViolation = 1000000000;
const cc_result ReturnCode_FileNotFound     = ENOENT;
const cc_result ReturnCode_PathNotFound     = 99999;
const cc_result ReturnCode_DirectoryExists  = EEXIST;

const char* Platform_AppNameSuffix = " ZehOS";
cc_uint8 Platform_Flags = PLAT_FLAG_SINGLE_PROCESS | PLAT_FLAG_APP_EXIT;
cc_bool  Platform_ReadonlyFilesystem;
#include "_PlatformBase.h"

/*########################################################################################################################*
*-----------------------------------------------------Main entrypoint-----------------------------------------------------*
*#########################################################################################################################*/
#include "main_impl.h"

int main(void) {
	/* argv des Loaders ist nur ["app"]; ohne Launcher startet --singleplayer direkt eine erzeugte Welt. */
	char* arguments[] = { "ClassiCube", DEFAULT_SINGLEPLAYER_ARG, NULL };
	cc_result res;
	SetupProgram(2, arguments);

	do {
		res = RunProgram(2, arguments);
	} while (Window_Main.Exists);

	Window_Free();
	Process_Exit(res);
	return res;
}


/*########################################################################################################################*
*------------------------------------------------------Logging/Time-------------------------------------------------------*
*#########################################################################################################################*/
void Platform_Log(const char* msg, int len) {
	fwrite(msg, 1, len, stdout);
	fputc('\n', stdout);
}

TimeMS DateTime_CurrentUTC(void) {
	struct timeval cur;
	gettimeofday(&cur, NULL);
	return (cc_uint64)cur.tv_sec + UNIX_EPOCH_SECONDS;
}

void DateTime_CurrentLocal(struct cc_datetime* t) {
	struct timeval cur;
	struct tm loc_time;
	gettimeofday(&cur, NULL);
	/* ZehOS kennt keine Zeitzone; die Ortszeit ist UTC. */
	gmtime_r(&cur.tv_sec, &loc_time);

	t->year   = loc_time.tm_year + 1900;
	t->month  = loc_time.tm_mon  + 1;
	t->day    = loc_time.tm_mday;
	t->hour   = loc_time.tm_hour;
	t->minute = loc_time.tm_min;
	t->second = loc_time.tm_sec;
}


/*########################################################################################################################*
*--------------------------------------------------------Stopwatch--------------------------------------------------------*
*#########################################################################################################################*/
cc_uint64 Stopwatch_Measure(void) {
	struct timespec cur;
	clock_gettime(CLOCK_MONOTONIC, &cur);
	return (cc_uint64)cur.tv_sec * 1000000ULL + cur.tv_nsec / 1000;
}

cc_uint64 Stopwatch_ElapsedMicroseconds(cc_uint64 beg, cc_uint64 end) {
	if (end < beg) return 0;
	return end - beg;
}


/*########################################################################################################################*
*-----------------------------------------------------Directory/File------------------------------------------------------*
*#########################################################################################################################*/
void Platform_EncodePath(cc_filepath* dst, const cc_string* path) {
	String_EncodeUtf8(dst->buffer, path);
}

void Platform_DecodePath(cc_string* dst, const cc_filepath* path) {
	const char* str = path->buffer;
	String_AppendUtf8(dst, str, String_Length(str));
}

void Directory_GetCachePath(cc_string* path) { }

/* Der Paketordner ist nur lesbar. ClassiCube legt beim Start Ordner fuer spaetere Schreibzugriffe an (maps,
   texturecache, audio) und zeigte dafuer rote Warnungen; die Schreibzugriffe selbst melden ihren eigenen Fehler. */
cc_result Directory_Create2(const cc_filepath* path) {
	return EEXIST;
}

int File_Exists(const cc_filepath* path) {
	struct stat sb;
	return stat(path->buffer, &sb) == 0 && S_ISREG(sb.st_mode);
}

/* Der Loader liest keine Verzeichnisse (fd_readdir fehlt); ClassiCube behandelt den Fehler wie einen leeren Ordner. */
cc_result Directory_Enum(const cc_string* dirPath, void* obj, Directory_EnumCallback callback) {
	return ERR_NOT_SUPPORTED;
}

static cc_result File_Do(cc_file* file, const char* path, int mode) {
	*file = open(path, mode, 0644);
	return *file == -1 ? errno : 0;
}

cc_result File_Open(cc_file* file, const cc_filepath* path) {
	return File_Do(file, path->buffer, O_RDONLY);
}
cc_result File_Create(cc_file* file, const cc_filepath* path) {
	return File_Do(file, path->buffer, O_RDWR | O_CREAT | O_TRUNC);
}
cc_result File_OpenOrCreate(cc_file* file, const cc_filepath* path) {
	return File_Do(file, path->buffer, O_RDWR | O_CREAT);
}

cc_result File_Read(cc_file file, void* data, cc_uint32 count, cc_uint32* bytesRead) {
	ssize_t res = read(file, data, count);
	if (res == -1) { *bytesRead = 0; return errno; }
	*bytesRead = (cc_uint32)res; return 0;
}

cc_result File_Write(cc_file file, const void* data, cc_uint32 count, cc_uint32* bytesWrote) {
	ssize_t res = write(file, data, count);
	if (res == -1) { *bytesWrote = 0; return errno; }
	*bytesWrote = (cc_uint32)res; return 0;
}

cc_result File_Close(cc_file file) {
	return close(file) == -1 ? errno : 0;
}

cc_result File_Seek(cc_file file, int offset, int seekType) {
	static cc_uint8 modes[3] = { SEEK_SET, SEEK_CUR, SEEK_END };
	return lseek(file, offset, modes[seekType]) == -1 ? errno : 0;
}

cc_result File_Position(cc_file file, cc_uint32* pos) {
	off_t res = lseek(file, 0, SEEK_CUR);
	if (res == -1) { *pos = 0; return errno; }
	*pos = (cc_uint32)res; return 0;
}

cc_result File_Length(cc_file file, cc_uint32* len) {
	struct stat st;
	if (fstat(file, &st) == -1) { *len = -1; return errno; }
	*len = (cc_uint32)st.st_size; return 0;
}


/*########################################################################################################################*
*--------------------------------------------------------Threading--------------------------------------------------------*
*#########################################################################################################################*/
void Thread_Sleep(cc_uint32 milliseconds) { zehos_sleep_ms(milliseconds); }


/*########################################################################################################################*
*-----------------------------------------------------Process/Module------------------------------------------------------*
*#########################################################################################################################*/
cc_result Process_StartGame2(const cc_string* args, int numArgs) {
	return SetGameArgs(args, numArgs);
}

void Process_Exit(cc_result code) { exit(code); }


/*########################################################################################################################*
*--------------------------------------------------------Platform---------------------------------------------------------*
*#########################################################################################################################*/
void Platform_Free(void) { }

cc_bool Platform_DescribeError(cc_result res, cc_string* dst) {
	const char* msg;
	if (res >= 1000) return false;

	msg = strerror((int)res);
	if (!msg) return false;
	String_AppendUtf8(dst, msg, String_Length(msg));
	return true;
}

void Platform_Init(void) {
	/* Paketdateien sind nur lesbar; ClassiCube legt dann weder Chatprotokolle noch Texturcache an. */
	Platform_ReadonlyFilesystem = true;
}


/*########################################################################################################################*
*-----------------------------------------------------Configuration-------------------------------------------------------*
*#########################################################################################################################*/
int Platform_GetCommandLineArgs(int argc, STRING_REF char** argv, cc_string* args) {
	int i, count;
	argc--; argv++; /* Programmname ueberspringen */
	if (gameHasArgs) return GetGameArgs(args);

	count = min(argc, GAME_MAX_CMDARGS);
	for (i = 0; i < count; i++)
	{
		args[i] = String_FromReadonly(argv[i]);
	}
	return count;
}

cc_result Platform_SetDefaultCurrentDirectory(void) { return 0; }
