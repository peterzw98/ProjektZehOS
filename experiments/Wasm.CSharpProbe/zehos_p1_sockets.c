// ZehOS: WASIp1 kennt keine Sockets; Mono-Debugger und System.Native referenzieren sie trotzdem.
// Jeder Aufruf scheitert kontrolliert, statt ein unbekanntes Import zu erzeugen.
// Typen bewusst vereinfacht: WASIp1 liefert weder netdb.h noch eine vollstaendige sys/socket.h.
#include <errno.h>
typedef unsigned int zehos_socklen;
#define ZEHOS_EAI_FAIL (-4)
int socket(int d, int t, int p) { (void)d; (void)t; (void)p; errno = ENOSYS; return -1; }
int bind(int s, const void* a, zehos_socklen l) { (void)s; (void)a; (void)l; errno = ENOSYS; return -1; }
int connect(int s, const void* a, zehos_socklen l) { (void)s; (void)a; (void)l; errno = ENOSYS; return -1; }
int listen(int s, int b) { (void)s; (void)b; errno = ENOSYS; return -1; }
int getsockname(int s, void* a, zehos_socklen* l) { (void)s; (void)a; (void)l; errno = ENOSYS; return -1; }
int setsockopt(int s, int lv, int n, const void* v, zehos_socklen l) { (void)s; (void)lv; (void)n; (void)v; (void)l; errno = ENOSYS; return -1; }
int getaddrinfo(const char* n, const char* s, const void* h, void** r) { (void)n; (void)s; (void)h; (void)r; return ZEHOS_EAI_FAIL; }
void freeaddrinfo(void* r) { (void)r; }
int getnameinfo(const void* a, zehos_socklen l, char* h, zehos_socklen hl, char* s, zehos_socklen sl, int f) { (void)a; (void)l; (void)h; (void)hl; (void)s; (void)sl; (void)f; return ZEHOS_EAI_FAIL; }
long recvfrom(int s, void* b, unsigned long n, int f, void* a, zehos_socklen* l) { (void)s; (void)b; (void)n; (void)f; (void)a; (void)l; errno = ENOSYS; return -1; }
long sendto(int s, const void* b, unsigned long n, int f, const void* a, zehos_socklen l) { (void)s; (void)b; (void)n; (void)f; (void)a; (void)l; errno = ENOSYS; return -1; }
int getpeername(int s, void* a, zehos_socklen* l) { (void)s; (void)a; (void)l; errno = ENOSYS; return -1; }
// Interne Hilfsfunktion der WASIp2-libc; unter WASIp1 gibt es keine Socket-Deskriptoren.
_Bool descriptor_table_get_ref(int fd, void** entry) { (void)fd; (void)entry; return 0; }
