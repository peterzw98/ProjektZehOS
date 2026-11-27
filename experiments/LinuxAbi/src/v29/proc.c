/* V29 /proc-Minimum (Ladybird #636): liest die Dateien so, wie Ladybird (LibCore Process::is_being_debugged,
 * Platform/ProcessStatisticsLinux.cpp) und übliche Bibliotheken sie lesen: /proc/self/status, /proc/stat mit
 * lseek(0) und erneutem Lesen, /proc/<pid>/stat und status eines Kindes, /proc/<pid>/exe, maps, meminfo,
 * cpuinfo. Nur deterministischer Text; das Kind wartet auf das Ende einer Pipe und schreibt nichts. */
#define _GNU_SOURCE
#include "../s3/util.h"
#include <fcntl.h>
#include <spawn.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/wait.h>
#include <time.h>

extern char **environ;
static char buf[16384];

static ssize_t slurp(const char *path)
{
    int fd = open(path, O_RDONLY | O_CLOEXEC);
    if (fd < 0) return -errno;
    ssize_t n = 0, r;
    while ((r = read(fd, buf + n, sizeof buf - 1 - n)) > 0) n += r;
    close(fd);
    buf[n] = 0;
    return r < 0 ? -errno : n;
}

/* Wert einer Zeile "Name:\t..." wie Ladybird (split an ':', Zahl nach Leerraum); -1, wenn sie fehlt. */
static long field(const char *name)
{
    size_t len = strlen(name);
    for (char *line = buf; line && *line; line = strchr(line, '\n') ? strchr(line, '\n') + 1 : NULL) {
        if (strncmp(line, name, len) == 0 && line[len] == ':') return strtol(line + len + 1, NULL, 10);
    }
    return -1;
}

int main(int argc, char **argv)
{
    if (argc > 1 && strcmp(argv[1], "--kind") == 0) {
        char c;
        while (read(0, &c, 1) > 0) {}
        return 7;
    }

    INIT();
    char self[512];
    ssize_t sn = readlink("/proc/self/exe", self, sizeof self - 1);
    NEED(sn > 0);
    self[sn] = 0;

    NEED(slurp("/proc/self/status") > 0);
    SHOWI("TracerPid", field("TracerPid"));
    SHOW("Pid = getpid", field("Pid") == getpid());
    SHOW("RssAnon > 0", field("RssAnon") > 0);
    SHOW("RssShmem da", field("RssShmem") >= 0);
    SHOW("Threads 1", field("Threads") == 1);

    /* Ladybird update_process_statistics: eine Datei, seek(0), read_some, sscanf mit 5 Werten. */
    int fd = open("/proc/stat", O_RDONLY | O_CLOEXEC);
    NEED(fd >= 0);
    int u = 0, s = 0, idle1 = -1, irq = 0, sirq = 0, idle2 = -1;
    ssize_t n = read(fd, buf, 1023);
    NEED(n > 0);
    buf[n] = 0;
    SHOWI("proc/stat Felder", sscanf(buf, "cpu %d %*d %d %d %*d %d %d", &u, &s, &idle1, &irq, &sirq));
    struct timespec t = { 0, 50 * 1000 * 1000 };
    nanosleep(&t, NULL);
    SHOWI("lseek 0", lseek(fd, 0, SEEK_SET));
    n = read(fd, buf, 1023);
    NEED(n > 0);
    buf[n] = 0;
    SHOWI("proc/stat Felder neu", sscanf(buf, "cpu %d %*d %d %d %*d %d %d", &u, &s, &idle2, &irq, &sirq));
    SHOW("idle waechst", idle2 > idle1);
    close(fd);

    /* Kind mit stdin an einer Pipe: läuft, bis die Pipe zu ist. */
    int p[2];
    NEED(pipe2(p, O_CLOEXEC) == 0);
    posix_spawn_file_actions_t fa;
    posix_spawn_file_actions_init(&fa);
    posix_spawn_file_actions_adddup2(&fa, p[0], 0);
    char *av[] = { self, "--kind", NULL };
    pid_t kid;
    NEED(posix_spawn(&kid, self, &fa, NULL, av, environ) == 0);
    close(p[0]);
    char path[64];
    snprintf(path, sizeof path, "/proc/%d/stat", (int)kid);
    NEED(slurp(path) > 0);
    unsigned long ut = 9, st = 9;
    SHOWI("pid/stat Felder", sscanf(buf, "%*d %*s %*c %*d %*d %*d %*d %*d %*u %*u %*u %*u %*u %lu %lu", &ut, &st));
    char *rp = strrchr(buf, ')');
    int ppid = -1;
    char state = '?';
    SHOW("pid/stat ppid", rp && sscanf(rp + 2, "%c %d", &state, &ppid) == 2 && ppid == getpid());
    snprintf(path, sizeof path, "/proc/%d/status", (int)kid);
    NEED(slurp(path) > 0);
    SHOW("Kind RssAnon da", field("RssAnon") >= 0);
    SHOW("Kind PPid", field("PPid") == getpid());
    char kexe[512];
    snprintf(path, sizeof path, "/proc/%d/exe", (int)kid);
    ssize_t kn = readlink(path, kexe, sizeof kexe - 1);
    SHOW("Kind exe = eigenes", kn == sn && memcmp(kexe, self, (size_t)sn) == 0);
    close(p[1]);
    int ws = 0;
    SHOW("Kind Ende 7", waitpid(kid, &ws, 0) == kid && WIFEXITED(ws) && WEXITSTATUS(ws) == 7);
    snprintf(path, sizeof path, "/proc/%d/stat", (int)kid);
    SHOWI("abgeholt errno", -slurp(path));

    NEED(slurp("/proc/self/maps") > 0);
    SHOW("maps [stack]", strstr(buf, " [stack]\n") != NULL);
    SHOW("maps rw-p", strstr(buf, " rw-p 00000000 00:00 0") != NULL);
    NEED(slurp("/proc/meminfo") > 0);
    SHOW("MemTotal > 0", field("MemTotal") > 0);
    SHOW("MemFree <= MemTotal", field("MemFree") <= field("MemTotal"));
    NEED(slurp("/proc/cpuinfo") > 0);
    SHOW("cpuinfo processor 0", strncmp(buf, "processor\t: 0\n", 14) == 0);

    struct stat sb;
    T("stat /proc/self/status", stat("/proc/self/status", &sb));
    SHOWI("Modus", (long)sb.st_mode);
    T("open O_WRONLY", open("/proc/self/status", O_WRONLY));
    T("open /proc/999999/stat", open("/proc/999999/stat", O_RDONLY));
    return 29;
}
