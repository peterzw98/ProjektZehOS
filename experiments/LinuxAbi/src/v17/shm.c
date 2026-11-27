/* Ladybird V17 (AnonymousBuffer für Bitmaps): geteilter Speicher über memfd.
 * memfd_create, ftruncate, fstat, MAP_SHARED: Schreiben über die Abbildung ist über read sichtbar und umgekehrt. Die memfd geht per
 * SCM_RIGHTS an ein Kind (posix_spawn, Socket als fd 3); der Elternprozess schließt seine Nummer danach und behält nur die Abbildung.
 * Das Kind bildet die Datei selbst MAP_SHARED ab, kopiert die ersten vier Bytes nach +0x100, schreibt "KIND" dahinter, schließt seine
 * Nummer und endet. Der Elternprozess sieht beides in seiner Abbildung (dieselben Frames, nach close aller Nummern gehalten von der
 * Abbildung). Dazu Siegel (F_GET_SEALS/F_ADD_SEALS, EBUSY, EPERM) wie Linux 6.12.
 * Das Kind ist ein statisches Mini-ELF, das dieses Programm ins Arbeitsverzeichnis schreibt (wie v16/unix, aber mit
 * getrennten Segmenten R+X und R+W: ZehOS lehnt W+X ab). */
#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <spawn.h>
#include <stdio.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <sys/wait.h>
#include <unistd.h>

extern char **environ;

#define BASE 0x400000u
#define CODE 0xb0u /* hinter zwei Programmköpfen */
#define D (BASE + 0x1200u) /* Datensegment RW ab Dateiversatz 0x200 */
#define MSG (D + 0)
#define IOV (D + 64)
#define CTRL (D + 80)
#define BYTE (D + 112)
#define FILESIZE 0x300u

static unsigned char img[FILESIZE];
static int len;

static void b1(int v) { img[CODE + len++] = (unsigned char)v; }
static void b4(unsigned v) { for (int i = 0; i < 4; i++) b1((int)((v >> (8 * i)) & 0xff)); }
static void movr(int op, unsigned v) { b1(op); b4(v); } /* B8 eax, BF edi, BE esi, BA edx */
static void sys(void) { b1(0x0f); b1(0x05); }
static void exit_block(int code) { movr(0xb8, 231); movr(0xbf, (unsigned)code); sys(); } /* 12 Bytes */
static void put64(unsigned off, unsigned long v) { for (int i = 0; i < 8; i++) img[off + i] = (unsigned char)(v >> (8 * i)); }

/* Kind: recvmsg mit SCM_RIGHTS (sonst exit 80), mmap(0, 4096, RW, MAP_SHARED, fd, 0) (Fehler: exit 81), kopiert [p] nach [p+0x100],
 * "KIND" nach [p+0x104], close(fd), ein Byte auf fd 3 als Fertigmeldung, exit_group(0). */
static int write_child(const char *path)
{
    memset(img, 0, sizeof img);
    len = 0;
    movr(0xb8, 47); movr(0xbf, 3); movr(0xbe, MSG); b1(0x31); b1(0xd2); sys(); /* recvmsg(3, &msg, 0) */
    b1(0x48); b1(0x83); b1(0xf8); b1(1);                                       /* cmp rax, 1 */
    b1(0x74); b1(12); exit_block(80);                                          /* je weiter */
    b1(0x44); b1(0x8b); b1(0x04); b1(0x25); b4(CTRL + 16);                     /* mov r8d, [CMSG_DATA] */
    movr(0xbf, 0); movr(0xbe, 4096); movr(0xba, 3);                            /* addr 0, len, PROT_READ|PROT_WRITE */
    b1(0x41); b1(0xba); b4(1);                                                 /* mov r10d, MAP_SHARED */
    b1(0x45); b1(0x31); b1(0xc9);                                              /* xor r9d, r9d */
    movr(0xb8, 9); sys();                                                      /* mmap */
    b1(0x48); b1(0x89); b1(0xc3);                                              /* mov rbx, rax */
    b1(0x48); b1(0x85); b1(0xc0);                                              /* test rax, rax */
    b1(0x79); b1(12); exit_block(81);                                          /* jns weiter */
    b1(0x8b); b1(0x0b);                                                        /* mov ecx, [rbx] */
    b1(0x89); b1(0x8b); b4(0x100);                                             /* mov [rbx+0x100], ecx */
    b1(0xc7); b1(0x83); b4(0x104); b4('K' | 'I' << 8 | 'N' << 16 | 'D' << 24); /* mov dword [rbx+0x104], "KIND" */
    b1(0x44); b1(0x89); b1(0xc7); movr(0xb8, 3); sys();                        /* close(r8d) */
    movr(0xb8, 1); movr(0xbf, 3); movr(0xbe, BYTE); movr(0xba, 1); sys();      /* write(3, byte, 1) */
    exit_block(0);

    /* msghdr: iov, iovlen 1, control, controllen 32; iovec: BYTE, 1. */
    put64(0x200 + 16, IOV); put64(0x200 + 24, 1); put64(0x200 + 32, CTRL); put64(0x200 + 40, 32);
    put64(0x200 + 64, BYTE); put64(0x200 + 72, 1);

    /* Zwei PT_LOAD: Code R+X (Datei 0..0x200), Daten R+W (Datei 0x200..0x300 nach 0x401200); ZehOS lehnt W+X ab (W^X). */
    memcpy(img, "\x7f" "ELF\x02\x01\x01", 7);
    img[16] = 2; img[18] = 62; img[20] = 1;
    put64(24, BASE + CODE);
    img[32] = 64; img[52] = 64; img[54] = 56; img[56] = 2;
    img[64] = 1; img[68] = 5;
    put64(64 + 16, BASE); put64(64 + 24, BASE);
    put64(64 + 32, 0x200); put64(64 + 40, 0x200); put64(64 + 48, 0x1000);
    img[120] = 1; img[124] = 6;
    put64(120 + 8, 0x200); put64(120 + 16, D); put64(120 + 24, D);
    put64(120 + 32, FILESIZE - 0x200); put64(120 + 40, FILESIZE - 0x200); put64(120 + 48, 0x1000);
    int fd = open(path, O_WRONLY | O_CREAT | O_TRUNC, 0755);
    if (fd < 0) return -1;
    int ok = write(fd, img, sizeof img) == (ssize_t)sizeof img;
    return close(fd) == 0 && ok ? 0 : -1;
}

static int send_fd(int sock, int fd)
{
    char byte = 'F';
    struct iovec iov = { &byte, 1 };
    union { struct cmsghdr h; char b[CMSG_SPACE(sizeof(int))]; } u;
    memset(&u, 0, sizeof u);
    struct msghdr m = { 0 };
    m.msg_iov = &iov;
    m.msg_iovlen = 1;
    m.msg_control = u.b;
    m.msg_controllen = sizeof u.b;
    struct cmsghdr *c = CMSG_FIRSTHDR(&m);
    c->cmsg_level = SOL_SOCKET;
    c->cmsg_type = SCM_RIGHTS;
    c->cmsg_len = CMSG_LEN(sizeof(int));
    memcpy(CMSG_DATA(c), &fd, sizeof fd);
    return (int)sendmsg(sock, &m, 0);
}

static void show(const char *what, long r)
{
    printf("%s=%ld errno=%d\n", what, r, r < 0 ? errno : 0);
    errno = 0;
}

static int memfd(const char *name, unsigned flags) { return (int)syscall(SYS_memfd_create, name, flags); }

static void part_share(void)
{
    int fd = memfd("ladybird", MFD_CLOEXEC | MFD_ALLOW_SEALING);
    show("memfd_create", fd >= 3 ? 0 : fd);
    show("ftruncate", ftruncate(fd, 8192));
    struct stat st;
    show("fstat", fstat(fd, &st));
    printf("size=%ld regular=%d mode=%o\n", (long)st.st_size, S_ISREG(st.st_mode), (unsigned)(st.st_mode & 0777));

    char *p = mmap(NULL, 8192, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
    show("mmap", p == MAP_FAILED ? -1 : 0);
    strcpy(p, "ELTERN");
    char buf[16] = { 0 };
    show("pread", pread(fd, buf, 6, 0));
    printf("datei=%s\n", buf);
    show("pwrite", pwrite(fd, "Datei", 5, 4096));
    printf("abbildung+4096=%.5s\n", p + 4096);
    char *q = mmap(NULL, 4096, PROT_READ, MAP_SHARED, fd, 4096);
    show("mmap2", q == MAP_FAILED ? -1 : 0);
    printf("zweite=%.5s\n", q);

    int sv[2];
    show("socketpair", socketpair(AF_UNIX, SOCK_STREAM, 0, sv));
    show("kind schreiben", write_child("kind"));
    posix_spawn_file_actions_t fa;
    posix_spawn_file_actions_init(&fa);
    posix_spawn_file_actions_adddup2(&fa, sv[1], 3);
    char *args[] = { "kind", NULL };
    pid_t pid;
    int err = posix_spawn(&pid, "kind", &fa, NULL, args, environ);
    posix_spawn_file_actions_destroy(&fa);
    show("posix_spawn", err == 0 ? 0 : -err);
    close(sv[1]);
    show("sendmsg", send_fd(sv[0], fd));
    show("close memfd", close(fd)); /* nur noch die Abbildungen halten die Datei */
    char b = 0;
    show("fertig", read(sv[0], &b, 1));
    int status = -1;
    show("waitpid", waitpid(pid, &status, 0) == pid ? 0 : -1);
    printf("kind exit=%d\n", WIFEXITED(status) ? WEXITSTATUS(status) : -1);
    printf("vom kind=%.8s\n", p + 0x100);
    p[0x200] = 'X';
    show("munmap", munmap(p, 8192));
    printf("zweite nach munmap=%.5s\n", q);
    show("munmap2", munmap(q, 4096));
    close(sv[0]);
    unlink("kind");
}

static void part_seals(void)
{
    int plain = memfd("ohne", 0);
    show("seals ohne", fcntl(plain, F_GET_SEALS));
    show("add ohne", fcntl(plain, F_ADD_SEALS, F_SEAL_SHRINK));
    close(plain);

    int fd = memfd("siegel", MFD_ALLOW_SEALING);
    show("seals", fcntl(fd, F_GET_SEALS));
    show("ftruncate", ftruncate(fd, 4096));
    char *w = mmap(NULL, 4096, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
    show("mmap rw", w == MAP_FAILED ? -1 : 0);
    w[0] = 'S';
    show("seal write belegt", fcntl(fd, F_ADD_SEALS, F_SEAL_WRITE));
    show("munmap", munmap(w, 4096));
    show("seal", fcntl(fd, F_ADD_SEALS, F_SEAL_SHRINK | F_SEAL_GROW | F_SEAL_WRITE));
    show("seals", fcntl(fd, F_GET_SEALS));
    show("write", write(fd, "x", 1));
    show("ftruncate klein", ftruncate(fd, 10));
    show("ftruncate gross", ftruncate(fd, 8192));
    char *rw = mmap(NULL, 4096, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
    show("mmap rw versiegelt", rw == MAP_FAILED ? -1 : 0);
    char *ro = mmap(NULL, 4096, PROT_READ, MAP_SHARED, fd, 0);
    show("mmap ro", ro == MAP_FAILED ? -1 : 0);
    printf("inhalt=%c\n", ro[0]);
    munmap(ro, 4096);
    close(fd);
}

/* V17-Review: mprotect umgeht das Schreibsiegel nicht (auch lesende Abbildungen vor dem Siegel zählen, VM_MAYWRITE), write nach
 * lseek(LONG_MAX - 1) läuft nicht über, mmap-Offset nahe 2^63. */
static void part_limits(void)
{
    int fd = memfd("grenzen", MFD_ALLOW_SEALING);
    show("ftruncate", ftruncate(fd, 8192));
    char *ro = mmap(NULL, 4096, PROT_READ, MAP_SHARED, fd, 0);
    show("seal write bei ro", fcntl(fd, F_ADD_SEALS, F_SEAL_WRITE));
    show("mprotect rw vor siegel", mprotect(ro, 4096, PROT_READ | PROT_WRITE));
    munmap(ro, 4096);
    char *none = mmap(NULL, 4096, PROT_NONE, MAP_SHARED, fd, 0);
    show("seal write bei none", fcntl(fd, F_ADD_SEALS, F_SEAL_WRITE));
    munmap(none, 4096);
    show("seal write", fcntl(fd, F_ADD_SEALS, F_SEAL_WRITE));
    char *s = mmap(NULL, 8192, PROT_READ, MAP_SHARED, fd, 0);
    show("mprotect rw versiegelt", mprotect(s, 4096, PROT_READ | PROT_WRITE));
    show("mprotect r versiegelt", mprotect(s, 4096, PROT_READ));
    show("munmap mitte", munmap(s + 4096, 4096));
    show("mprotect rw rest", mprotect(s, 4096, PROT_READ | PROT_WRITE));
    munmap(s, 4096);
    close(fd);

    int g = memfd("ueberlauf", 0);
    show("lseek", lseek(g, 0x7ffffffffffffffeL, SEEK_SET) == 0x7ffffffffffffffeL ? 0 : -1);
    show("write ueberlauf", write(g, "abcd", 4));
    void *m = mmap(NULL, 4096, PROT_READ, MAP_SHARED, g, 0x7ffffffffffff000UL);
    show("mmap 2^63-4096", m == MAP_FAILED ? -1 : 0);
    m = mmap(NULL, 4096, PROT_READ, MAP_SHARED, g, 0x8000000000000000UL);
    show("mmap 2^63", m == MAP_FAILED ? -1 : 0);
    m = mmap(NULL, 4096, PROT_READ, MAP_SHARED, g, 0x7fffffffffffe000UL);
    show("mmap 2^63-8192", m == MAP_FAILED ? -1 : 0);
    if (m != MAP_FAILED)
    {
        munmap(m, 4096);
    }
    close(g);
}

int main(void)
{
    setvbuf(stdout, NULL, _IONBF, 0);
    part_share();
    part_seals();
    part_limits();
    printf("ende\n");
    return 0;
}
