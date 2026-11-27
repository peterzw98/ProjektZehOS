/* V25b: Ein statisches Programm startet dynamisch gelinkte Programme, wie Ladybird seine Helfer: posix_spawn
 * (musl: clone(CLONE_VM|CLONE_VFORK) + execve), vfork + execve und zuletzt execve im eigenen Prozess (Abbild
 * wird ersetzt). Die Programme liegen per Buendel-Datei unter /bin, ld-musl und die Bibliotheken unter /lib und
 * /usr/lib. Der Elternteil schreibt erst nach wait4, damit die Ausgabe deterministisch bleibt. */
#define _GNU_SOURCE
#include "../s3/util.h"
#include <spawn.h>
#include <string.h>
#include <sys/wait.h>

extern char **environ;

static void report(const char *name, pid_t pid)
{
    int status = 0;
    NEED(waitpid(pid, &status, 0) == pid);
    if (WIFEXITED(status)) printf("%s exit %d\n", name, WEXITSTATUS(status));
    else printf("%s signal %d\n", name, WIFSIGNALED(status) ? WTERMSIG(status) : -1);
}

int main(void)
{
    INIT();

    /* 1. posix_spawn eines C++-Programms (libstdc++, libgcc_s, Threads). */
    char *cxx[] = { "dyn_cxx", NULL };
    pid_t pid = 0;
    int rc = posix_spawn(&pid, "/bin/dyn_cxx", NULL, NULL, cxx, environ);
    /* Nach dem Ende der vfork-Phase laufen Kind und Elternteil parallel: rc erst nach wait4 ausgeben. */
    if (rc == 0) report("dyn_cxx", pid);
    printf("posix_spawn dyn_cxx rc=%d\n", rc);

    /* 2. Fehlender Interpreter: ENOENT kommt beim Elternteil an, kein halbes Kind. */
    char *broken[] = { "kaputt", NULL };
    rc = posix_spawn(&pid, "/bin/ohne_interp", NULL, NULL, broken, environ);
    printf("posix_spawn ohne_interp rc=%s\n", rc == ENOENT ? "ENOENT" : rc == 0 ? "0" : "anderer");
    if (rc == 0) report("ohne_interp", pid);

    /* 3. vfork + execve. */
    char *hello[] = { "dyn_hello", "v", "b", NULL };
    pid = vfork();
    if (pid == 0) {
        execve("/bin/dyn_hello", hello, environ);
        _exit(127);
    }
    NEED(pid > 0);
    report("dyn_hello", pid);

    /* 4. execve ohne vfork: das statische Abbild wird durch das dynamische ersetzt; dessen Exitcode zaehlt. */
    char *last[] = { "dyn_hello", "ende", "x", NULL };
    printf("execve selbst\n");
    execve("/bin/dyn_hello", last, environ);
    printf("execve fehler errno=%d\n", errno);
    return 9;
}
