/* Argumente, Umgebung und Hilfsvektor des Startstacks (Entwurf, Zeile S1). */
#include "util.h"
#include <elf.h>
#include <string.h>
#include <sys/auxv.h>
#include <unistd.h>

extern char **environ;

int main(int argc, char **argv)
{
    INIT();
    SHOWI("argc", argc);
    for (int i = 1; i < argc; i++) printf("argv[%d]=%s\n", i, argv[i]);
    int n = 0;
    for (char **e = environ; *e; e++) { printf("env=%s\n", *e); n++; }
    SHOWI("envc", n);
    SHOWI("AT_PAGESZ", getauxval(AT_PAGESZ));
    SHOWI("AT_PHENT", getauxval(AT_PHENT));
    SHOW("AT_PHNUM>0", getauxval(AT_PHNUM) > 0);
    SHOW("AT_PHDR!=0", getauxval(AT_PHDR) != 0);
    SHOW("AT_ENTRY!=0", getauxval(AT_ENTRY) != 0);
    SHOWI("AT_BASE", getauxval(AT_BASE));
    SHOW("AT_RANDOM!=0", getauxval(AT_RANDOM) != 0);
    SHOW("AT_UID==getuid", getauxval(AT_UID) == (unsigned long)getuid());
    SHOW("AT_EUID==geteuid", getauxval(AT_EUID) == (unsigned long)geteuid());
    SHOW("AT_GID==getgid", getauxval(AT_GID) == (unsigned long)getgid());
    SHOW("AT_EGID==getegid", getauxval(AT_EGID) == (unsigned long)getegid());
    SHOWI("AT_SECURE", getauxval(AT_SECURE));
    SHOW("AT_HWCAP!=0", getauxval(AT_HWCAP) != 0); /* x86-64: CPUID-EDX, nie 0 */
    const char *x = (const char *)getauxval(AT_EXECFN);
    SHOW("AT_EXECFN!=0", x != NULL);
    if (x) { const char *b = strrchr(x, '/'); printf("AT_EXECFN basename=%s\n", b ? b + 1 : x); }
    return 0;
}
