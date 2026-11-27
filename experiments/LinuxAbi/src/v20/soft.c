/* V20 (Ladybird, weiche Lücken): eventfd2 (Zähler, Semaphore, Überlauf, poll, Weitergabe an ein vfork-Kind), statfs/fstatfs (nur Werte,
 * die Linux und ZehOS gleich melden: Ergebnis, f_namelen, Magie der Pseudodateisysteme), inotify_init1/add_watch/rm_watch ohne
 * Dateiänderung (einziges Ereignis IN_IGNORED). Die Ausgabe ist mit Linux 6.12 gleich (expected/v20/soft.txt). */
#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <sys/eventfd.h>
#include <sys/inotify.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/statfs.h>
#include <sys/syscall.h>
#include <sys/wait.h>
#include <unistd.h>

static void show(const char *what, long r)
{
    printf("%s=%ld errno=%d\n", what, r, r < 0 ? errno : 0);
}

static short pollev(int fd, short ev)
{
    struct pollfd p = { fd, ev, 0 };
    return poll(&p, 1, 0) < 0 ? -1 : p.revents;
}

static long put(int fd, uint64_t v) { return write(fd, &v, sizeof v); }

static long take(int fd, uint64_t *v) { *v = 0; return read(fd, v, sizeof *v); }

static void eventfds(void)
{
    uint64_t v;
    show("eventfd2_badflag", eventfd(0, 0x40));
    int e = eventfd(3, EFD_CLOEXEC | EFD_NONBLOCK);
    show("eventfd2_ok", e >= 0);
    show("fd_cloexec", fcntl(e, F_GETFD));
    show("fl", fcntl(e, F_GETFL) & (O_ACCMODE | O_NONBLOCK));
    show("put4", put(e, 4));
    show("poll", pollev(e, POLLIN | POLLOUT));
    long r = take(e, &v);
    printf("take=%ld v=%llu\n", r, (unsigned long long)v);
    show("take_empty", take(e, &v));
    show("poll_empty", pollev(e, POLLIN | POLLOUT));
    char small[4];
    show("read_small", read(e, small, sizeof small));
    show("read_zero", read(e, small, 0));
    show("write_small", write(e, small, sizeof small));
    show("put_max", put(e, UINT64_MAX));
    show("put_full", put(e, UINT64_MAX - 1));
    show("put_over", put(e, 1));
    show("poll_full", pollev(e, POLLIN | POLLOUT));
    r = take(e, &v);
    printf("take_full=%ld high=%llx\n", r, (unsigned long long)(v >> 32));
    struct stat st;
    show("fstat", fstat(e, &st));
    printf("mode=%o size=%lld\n", (unsigned)st.st_mode, (long long)st.st_size);
    struct statfs sf;
    show("fstatfs", fstatfs(e, &sf));
    printf("anon_magic=%llx\n", (unsigned long long)sf.f_type);

    int s = eventfd(2, EFD_SEMAPHORE | EFD_NONBLOCK);
    r = take(s, &v);
    printf("sem1=%ld v=%llu\n", r, (unsigned long long)v);
    r = take(s, &v);
    printf("sem2=%ld v=%llu\n", r, (unsigned long long)v);
    show("sem3", take(s, &v));

    /* Weitergabe: ein vfork-Kind schreibt, die Eltern lesen blockierend (ohne EFD_NONBLOCK). */
    int b = eventfd(0, 0);
    pid_t pid = vfork();
    if (pid == 0) {
        put(b, 42);
        _exit(0);
    }
    int status = -1;
    show("wait", waitpid(pid, &status, 0) == pid);
    r = take(b, &v);
    printf("child=%ld v=%llu status=%d\n", r, (unsigned long long)v, status);
    close(e);
    close(s);
    close(b);
}

static void statfss(void)
{
    struct statfs sf;
    show("statfs_root", statfs("/", &sf));
    printf("namelen=%ld bsize_ok=%d\n", (long)sf.f_namelen, sf.f_bsize > 0);
    show("statfs_missing", statfs("/gibt-es-nicht", &sf));
    show("statfs_fault", syscall(SYS_statfs, "/", (void *)8)); /* direkt: musl kopiert sonst selbst und stürzt ab */
    show("fstatfs_badfd", fstatfs(77, &sf));
    int p[2];
    show("pipe", pipe(p));
    show("fstatfs_pipe", fstatfs(p[0], &sf));
    printf("pipe_magic=%llx\n", (unsigned long long)sf.f_type);
    close(p[0]);
    close(p[1]);
    int u[2];
    show("socketpair", socketpair(AF_UNIX, SOCK_STREAM, 0, u));
    show("fstatfs_sock", fstatfs(u[0], &sf));
    printf("sock_magic=%llx\n", (unsigned long long)sf.f_type);
    close(u[0]);
    close(u[1]);
}

static void inotifys(void)
{
    show("init1_badflag", inotify_init1(1));
    int n = inotify_init1(IN_CLOEXEC | IN_NONBLOCK);
    show("init1_ok", n >= 0);
    show("fd_cloexec", fcntl(n, F_GETFD));
    show("fl", fcntl(n, F_GETFL) & (O_ACCMODE | O_NONBLOCK));
    show("add_mask0", inotify_add_watch(n, "/", 0));
    show("add_addcreate", inotify_add_watch(n, "/", IN_MODIFY | IN_MASK_ADD | IN_MASK_CREATE));
    show("add_badfd", inotify_add_watch(77, "/", IN_MODIFY));
    show("add_notinotify", inotify_add_watch(1, "/", IN_MODIFY));
    show("add_missing", inotify_add_watch(n, "/gibt-es-nicht", IN_MODIFY));
    int w1 = inotify_add_watch(n, "/", IN_MODIFY | IN_CREATE);
    show("add_root", w1 > 0);
    show("add_same", inotify_add_watch(n, "/", IN_DELETE) == w1);
    show("add_create_again", inotify_add_watch(n, "/", IN_MODIFY | IN_MASK_CREATE));
    int f = open("v20.tmp", O_CREAT | O_WRONLY | O_TRUNC, 0644);
    show("tmp", f >= 0);
    show("add_onlydir", inotify_add_watch(n, "v20.tmp", IN_MODIFY | IN_ONLYDIR));
    int w2 = inotify_add_watch(n, "v20.tmp", IN_OPEN);
    show("add_file", w2 > 0 && w2 != w1);
    show("poll_none", pollev(n, POLLIN | POLLOUT));
    char buf[256];
    show("read_none", read(n, buf, sizeof buf));
    show("write", write(n, buf, 8));
    show("rm_bad", inotify_rm_watch(n, 12345));
    show("rm_file", inotify_rm_watch(n, w2));
    show("rm_again", inotify_rm_watch(n, w2));
    show("poll_ignored", pollev(n, POLLIN | POLLOUT));
    show("read_small", read(n, buf, 8));
    long r = read(n, buf, sizeof buf);
    struct inotify_event ev;
    memcpy(&ev, buf, sizeof ev);
    printf("read_ignored=%ld wd_ok=%d mask=%x len=%u\n", r, ev.wd == w2, ev.mask, ev.len);
    struct stat st;
    show("fstat", fstat(n, &st));
    printf("mode=%o\n", (unsigned)st.st_mode);
    close(f);
    unlink("v20.tmp");
    close(n);
}

int main(void)
{
    setvbuf(stdout, NULL, _IONBF, 0);
    eventfds();
    statfss();
    inotifys();
    printf("ende\n");
    return 0;
}
