/* ZehOS-Plattformschicht fuer ClassiCube (BSD-3-Clause wie ClassiCube, Copyright (c) 2014 - 2024, UnknownShadow200).
   Abgeleitet von src/msdos/Platform_MSDOS.c: ein Faden, keine Prozesse. Laeuft als WASIp1-Programm im
   ZehOS-Wasm-Loader; Paketdateien sind nur lesbar. Mit capability=storage landen Welten (maps/) und options.txt im
   App-Speicher /save, sonst scheitern Schreibversuche mit einem Fehlercode. TCP laeuft ueber die Netzfunktionen von
   zehos_v1 (nur mit capability=net im Paketmanifest). */
#define CC_NO_UPDATER
#define CC_NO_DYNLIB
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
#include <wasi/api.h>

#define ZEHOS_IMPORT(name) __attribute__((import_module("zehos_v1"), import_name(name)))
/* WASI kennt kein poll_oneoff im Loader, usleep der libc wuerde scheitern. */
ZEHOS_IMPORT("sleep_ms") void zehos_sleep_ms(cc_uint32 milliseconds);
/* Netz-ABI: Rueckgabe ist eine WASI-Fehlernummer (gleich den errno-Werten von wasi-libc), 0 bei Erfolg. */
ZEHOS_IMPORT("net_open") cc_int32 zehos_net_open(const char* host, cc_uint32 length, cc_int32 port, cc_int32 flags, cc_int32* handle);
ZEHOS_IMPORT("net_status") cc_int32 zehos_net_status(cc_int32 handle);
ZEHOS_IMPORT("net_send") cc_int32 zehos_net_send(cc_int32 handle, const cc_uint8* data, cc_uint32 length, cc_int32* count);
ZEHOS_IMPORT("net_recv") cc_int32 zehos_net_recv(cc_int32 handle, cc_uint8* data, cc_uint32 length, cc_int32* count);
ZEHOS_IMPORT("net_close") cc_int32 zehos_net_close(cc_int32 handle);

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
	/* argv des Loaders ist nur ["app"]. Ohne Argumente startet der Launcher (Singleplayer, Direct connect); er
	   uebergibt die Spielargumente ueber Process_StartGame2, der zweite RunProgram-Durchlauf startet das Spiel. */
	char* arguments[] = { "ClassiCube", NULL };
	cc_result res;
	SetupProgram(1, arguments);

	do {
		res = RunProgram(1, arguments);
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
/* App-Speicher (Etappe 13.4): Der Loader gibt ihn nur mit capability=storage, Anmeldung und eingebundenem /data als
   zweiten Preopen "/save" (fd 4) frei. Nur Welten und Optionen wandern dorthin; alles andere bleibt im Paket. */
#define SAVE_FD     4
#define SAVE_PREFIX "/save/"
static cc_bool saveAvailable;

static cc_bool DetectSave(void) {
	__wasi_prestat_t prestat;
	char name[5];
	if (__wasi_fd_prestat_get(SAVE_FD, &prestat) || prestat.tag != __WASI_PREOPENTYPE_DIR) return false;
	if (prestat.u.dir.pr_name_len != sizeof(name)) return false;
	return !__wasi_fd_prestat_dir_name(SAVE_FD, (uint8_t*)name, sizeof(name)) && !memcmp(name, "/save", sizeof(name));
}

static cc_bool IsSaveTarget(const cc_string* path) {
	static const cc_string maps = String_FromConst("maps/");
	return String_CaselessEqualsConst(path, "options.txt") || String_CaselessEqualsConst(path, "maps")
		|| String_CaselessStarts(path, &maps);
}

static cc_bool IsSavePath(const char* path) {
	return !strncmp(path, SAVE_PREFIX, sizeof(SAVE_PREFIX) - 1);
}

void Platform_EncodePath(cc_filepath* dst, const cc_string* path) {
	char* str = dst->buffer;
	if (saveAvailable && IsSaveTarget(path)) {
		Mem_Copy(str, SAVE_PREFIX, sizeof(SAVE_PREFIX) - 1);
		str += sizeof(SAVE_PREFIX) - 1;
	}
	String_EncodeUtf8(str, path);
}

void Platform_DecodePath(cc_string* dst, const cc_filepath* path) {
	const char* str = path->buffer;
	String_AppendUtf8(dst, str, String_Length(str));
}

void Directory_GetCachePath(cc_string* path) { }

/* Der Paketordner ist nur lesbar. ClassiCube legt beim Start Ordner fuer spaetere Schreibzugriffe an (maps,
   texturecache, audio) und zeigte dafuer rote Warnungen; die Schreibzugriffe selbst melden ihren eigenen Fehler.
   Nur unter /save entsteht der Ordner wirklich. */
cc_result Directory_Create2(const cc_filepath* path) {
	if (!IsSavePath(path->buffer)) return EEXIST;
	return mkdir(path->buffer, 0755) == -1 ? errno : 0;
}

int File_Exists(const cc_filepath* path) {
	struct stat sb;
	return stat(path->buffer, &sb) == 0 && S_ISREG(sb.st_mode);
}

/* Ordner des Pakets liest der Loader nicht (fd_readdir nur unter /save); ClassiCube behandelt den Fehler wie einen
   leeren Ordner. Unter /save wie Platform_Posix.c. */
cc_result Directory_Enum(const cc_string* dirPath, void* obj, Directory_EnumCallback callback) {
	cc_string path; char pathBuffer[FILENAME_SIZE];
	cc_filepath str;
	DIR* dirPtr;
	struct dirent* entry;
	char* src;
	cc_result res;

	Platform_EncodePath(&str, dirPath);
	if (!IsSavePath(str.buffer)) return ERR_NOT_SUPPORTED;
	dirPtr = opendir(str.buffer);
	if (!dirPtr) return errno;

	/* readdir laesst errno am Ende unveraendert; deshalb vor jedem Aufruf zuruecksetzen. */
	errno = 0;
	String_InitArray(path, pathBuffer);

	while ((entry = readdir(dirPtr))) {
		src = entry->d_name;
		if (src[0] == '.' && (src[1] == '\0' || (src[1] == '.' && src[2] == '\0'))) continue;

		path.length = 0;
		String_Format1(&path, "%s/", dirPath);
		String_AppendUtf8(&path, src, String_Length(src));
		callback(&path, obj, entry->d_type == DT_DIR);
		errno = 0;
	}

	res = errno;
	closedir(dirPtr);
	return res;
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
*---------------------------------------------------------Socket----------------------------------------------------------*
*#########################################################################################################################*/
/* Der Kernel loest Namen erst in net_open auf und verbindet dort sofort. cc_sockaddr traegt deshalb nur Port (2 Byte,
   Host-Reihenfolge) und Namen; Socket_Create oeffnet die Verbindung, Socket_Connect meldet nur "laeuft". */
const cc_result ReturnCode_SocketInProgess  = EINPROGRESS;
const cc_result ReturnCode_SocketWouldBlock = EAGAIN;
const cc_result ReturnCode_SocketDropped    = ECONNRESET;

/* Groesste Einzeluebertragung der Netz-ABI */
#define NET_MAX_TRANSFER (64 * 1024)
#define NET_MAX_HOST 253

static cc_bool StoreHost(const cc_string* host, int port, cc_sockaddr* dst) {
	int i;
	if (host->length < 1 || host->length > NET_MAX_HOST || port < 1 || port > 65535) return false;
	for (i = 0; i < host->length; i++) {
		char c = host->buffer[i];
		if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9') && c != '.' && c != '-')
			return false;
	}
	dst->data[0] = (cc_uint8)port;
	dst->data[1] = (cc_uint8)(port >> 8);
	Mem_Copy(dst->data + 2, host->buffer, host->length);
	dst->size = 2 + host->length;
	return true;
}

static cc_bool ParseIPv4(const cc_string* ip, int port, cc_sockaddr* dst) {
	cc_uint32 raw;
	return ParseIPv4Address(ip, &raw) && StoreHost(ip, port, dst);
}

/* Die Netz-ABI kennt nur IPv4 und DNS-Namen. */
static cc_bool ParseIPv6(const char* ip, int port, cc_sockaddr* dst) { return false; }

static cc_result ParseHost(const char* host, int port, cc_sockaddr* addrs, int* numValidAddrs) {
	cc_string name = String_FromReadonly(host);
	if (!StoreHost(&name, port, &addrs[0])) return ERR_INVALID_ARGUMENT;
	*numValidAddrs = 1;
	return 0;
}

cc_bool SockAddr_ToString(const cc_sockaddr* addr, cc_string* dst) {
	int port = addr->data[0] | (addr->data[1] << 8);
	String_AppendAll(dst, addr->data + 2, addr->size - 2);
	String_Format1(dst, ":%i", &port);
	return true;
}

cc_result Socket_Create(cc_socket* s, cc_sockaddr* addr) {
	int port = addr->data[0] | (addr->data[1] << 8);
	cc_int32 handle = 0;
	cc_result res = zehos_net_open((const char*)addr->data + 2, addr->size - 2, port, 0, &handle);
	*s = res ? -1 : handle;
	return res;
}

cc_result Socket_SetNonBlocking(cc_socket s, cc_bool nonblocking) { return 0; }

void Socket_Close(cc_socket s) {
	if (s >= 0) zehos_net_close(s);
}

cc_result Socket_Connect(cc_socket s, const void* addr, int addrSize) { return EINPROGRESS; }

cc_result Socket_Read(cc_socket s, cc_uint8* data, cc_uint32 count, cc_uint32* modified) {
	cc_int32 read = 0;
	cc_result res;
	*modified = 0;
	if (!count) return 0;
	res = zehos_net_recv(s, data, min(count, NET_MAX_TRANSFER), &read);
	if (res) return res;
	/* EOF: ClassiCube werte 0 Bytes erst nach 30 s Stille als Abbruch; ein beendeter Server soll sofort zaehlen. */
	if (read == 0) return ReturnCode_SocketDropped;
	*modified = (cc_uint32)read;
	return 0;
}

cc_result Socket_Write(cc_socket s, const cc_uint8* data, cc_uint32 count, cc_uint32* modified) {
	cc_int32 wrote = 0;
	cc_result res = zehos_net_send(s, data, min(count, NET_MAX_TRANSFER), &wrote);
	*modified = res ? 0 : (cc_uint32)wrote;
	return res;
}

/* net_status ist EAGAIN bis DNS und Verbindungsaufbau fertig sind. Lesbarkeit laesst sich ohne Lesen nicht pruefen;
   ClassiCube fragt READ nur im (abgeschalteten) HTTP-Client ab, verbunden gilt deshalb fuer beide Arten. */
cc_result Socket_Poll(cc_socket s, int timeoutMS, int mode, cc_bool* success) {
	cc_result res = zehos_net_status(s);
	*success = res == 0;
	return res == EAGAIN ? 0 : res;
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
	saveAvailable = DetectSave();
	/* Die Kennung steuert in ClassiCube nur Chatprotokolle und Texturcache, nicht Welten und Optionen. Beide gehoeren
	   nicht in die Quote von /save; sie bleiben deshalb auch mit App-Speicher aus. */
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
