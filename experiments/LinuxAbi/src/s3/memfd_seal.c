/* memfd_create und Versiegelung (F_ADD_SEALS/F_GET_SEALS): SEAL, SHRINK, GROW, WRITE;
 * Folgen für write, ftruncate, mmap; Weitergabe an ein Kind per fork (geteilte Seiten). */
#define _GNU_SOURCE
#include "util.h"
#include <fcntl.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <sys/wait.h>

#ifndef F_ADD_SEALS
#define F_ADD_SEALS 1033
#define F_GET_SEALS 1034
#define F_SEAL_SEAL 1
#define F_SEAL_SHRINK 2
#define F_SEAL_GROW 4
#define F_SEAL_WRITE 8
#endif

int main(void)
{
    INIT();
    long ps = sysconf(_SC_PAGESIZE);
    int fd = (int)syscall(SYS_memfd_create, "abi", MFD_CLOEXEC | MFD_ALLOW_SEALING);
    NEED(fd >= 0);
    SHOW("CLOEXEC gesetzt", (fcntl(fd, F_GETFD) & FD_CLOEXEC) != 0);
    SHOWI("Seals zu Beginn", fcntl(fd, F_GET_SEALS));
    struct stat st;
    NEED(fstat(fd, &st) == 0);
    SHOW("regulär, Größe 0", S_ISREG(st.st_mode) && st.st_size == 0);
    NEED(ftruncate(fd, ps) == 0);
    NEED(pwrite(fd, "inhalt", 6, 0) == 6);
    char *rw = mmap(NULL, ps, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
    NEED(rw != MAP_FAILED);
    SHOW("Seiten zu Beginn Null hinter dem Inhalt", rw[6] == 0 && rw[ps - 1] == 0);
    memcpy(rw + 6, "-map", 4);
    char buf[16] = {0};
    NEED(pread(fd, buf, 10, 0) == 10);
    SHOW("Schreiben über Abbildung sichtbar", memcmp(buf, "inhalt-map", 10) == 0);

    /* F_SEAL_WRITE bei vorhandener beschreibbarer geteilter Abbildung: EBUSY. */
    T("WRITE mit offener Schreibabbildung", fcntl(fd, F_ADD_SEALS, F_SEAL_WRITE));
    T("SHRINK|GROW (erlaubt)", fcntl(fd, F_ADD_SEALS, F_SEAL_SHRINK | F_SEAL_GROW));
    SHOWI("Seals", fcntl(fd, F_GET_SEALS));
    T("ftruncate kleiner (SHRINK)", ftruncate(fd, ps / 2));
    T("ftruncate größer (GROW)", ftruncate(fd, ps * 2));
    SHOWI("ftruncate gleich (erlaubt)", ftruncate(fd, ps));
    T("pwrite hinter das Ende (GROW)", pwrite(fd, "x", 1, ps));
    SHOWI("pwrite innerhalb", pwrite(fd, "I", 1, 0));
    NEED(munmap(rw, ps) == 0);

    T("WRITE, nun ohne Abbildung", fcntl(fd, F_ADD_SEALS, F_SEAL_WRITE));
    SHOWI("Seals", fcntl(fd, F_GET_SEALS));
    T("pwrite (WRITE)", pwrite(fd, "x", 1, 0));
    T("write (WRITE)", write(fd, "x", 1));
    T("mmap SHARED+WRITE (WRITE)", (long)(mmap(NULL, ps, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0) == MAP_FAILED ? -1 : 0));
    char *ro = mmap(NULL, ps, PROT_READ, MAP_SHARED, fd, 0);
    NEED(ro != MAP_FAILED);
    SHOW("mmap SHARED+READ geht, Inhalt", memcmp(ro, "Inhalt-map", 10) == 0);
    T("mprotect auf WRITE (WRITE)", mprotect(ro, ps, PROT_READ | PROT_WRITE));
    char *pv = mmap(NULL, ps, PROT_READ | PROT_WRITE, MAP_PRIVATE, fd, 0);
    NEED(pv != MAP_FAILED);
    pv[0] = 'P';
    SHOW("mmap PRIVATE+WRITE geht, Original unverändert", pv[0] == 'P' && ro[0] == 'I');
    T("Seals nach WRITE ändern: SEAL", fcntl(fd, F_ADD_SEALS, F_SEAL_SEAL));
    T("danach weiteres Siegel (SEAL)", fcntl(fd, F_ADD_SEALS, F_SEAL_SHRINK));
    SHOWI("Seals", fcntl(fd, F_GET_SEALS));
    T("ungültiges Siegel", fcntl(fd, F_ADD_SEALS, 0x4000));

    /* Kind sieht die Versiegelung über geerbten Deskriptor und dieselben Seiten. */
    pid_t c = fork();
    NEED(c >= 0);
    if (c == 0) {
        CNEED(fcntl(fd, F_GET_SEALS) == (F_SEAL_SEAL | F_SEAL_SHRINK | F_SEAL_GROW | F_SEAL_WRITE));
        CNEED(write(fd, "x", 1) == -1 && errno == EPERM);
        CNEED(memcmp(ro, "Inhalt-map", 10) == 0);
        _exit(0);
    }
    int ws = 0;
    NEED(waitpid(c, &ws, 0) == c);
    SHOW("Kind sieht dieselben Siegel", WIFEXITED(ws) && WEXITSTATUS(ws) == 0);
    close(fd);
    SHOW("Abbildung überlebt close", memcmp(ro, "Inhalt-map", 10) == 0);

    /* Ohne MFD_ALLOW_SEALING: versiegelt von Anfang an (F_SEAL_SEAL). */
    int nf = (int)syscall(SYS_memfd_create, "ohne", 0);
    NEED(nf >= 0);
    SHOW("CLOEXEC nicht gesetzt", (fcntl(nf, F_GETFD) & FD_CLOEXEC) == 0);
    SHOWI("Seals ohne ALLOW_SEALING", fcntl(nf, F_GET_SEALS));
    T("Siegel hinzufügen (SEAL)", fcntl(nf, F_ADD_SEALS, F_SEAL_WRITE));
    close(nf);

    T("memfd_create ungültiges Flag", syscall(SYS_memfd_create, "x", 0x80000000u));
    T("memfd_create ungültiger Name", syscall(SYS_memfd_create, (char *)1, 0));
    char lang[300];
    memset(lang, 'n', sizeof lang - 1);
    lang[sizeof lang - 1] = 0;
    T("memfd_create Name zu lang", syscall(SYS_memfd_create, lang, 0));
    return 0;
}
