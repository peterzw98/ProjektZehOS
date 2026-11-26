/* V3 termios: isatty auf fd 0-2, kanonisch mit Echo, Fenstergroesse, Rohmodus nur gespeichert, Vordergrundgruppe.
   Kein fflush: Die Zeilen erscheinen vor dem blockierenden fgets nur, wenn musl stdout zeilenweise puffert (Terminal). */
#include <stdio.h>
#include <string.h>
#include <termios.h>
#include <unistd.h>
#include <sys/ioctl.h>
int main(void)
{
    struct termios t, raw, back;
    struct winsize w;
    char line[256];
    printf("tty %d%d%d\n", isatty(0), isatty(1), isatty(2));
    if (tcgetattr(0, &t) != 0) { printf("tcgetattr?\n"); return 4; }
    printf("canon %d echo %d\n", !!(t.c_lflag & ICANON), !!(t.c_lflag & ECHO));
    if (ioctl(1, TIOCGWINSZ, &w) != 0) { printf("winsz?\n"); return 4; }
    printf("winsz %s %d %d\n", w.ws_row > 1 && w.ws_col > 20 ? "ok" : "falsch", w.ws_row, w.ws_col);
    raw = t;
    cfmakeraw(&raw);
    if (tcsetattr(0, TCSAFLUSH, &raw) != 0 || tcgetattr(2, &back) != 0) { printf("tcsetattr?\n"); return 4; }
    printf("raw gespeichert %d\n", !(back.c_lflag & ICANON) && !(back.c_lflag & ECHO));
    tcsetattr(0, TCSANOW, &t);
    printf("pgrp %d\n", tcgetpgrp(0) == getpid());
    if (!fgets(line, sizeof line, stdin)) { printf("EOF\n"); return 5; }
    line[strcspn(line, "\n")] = 0;
    printf("= %s\n", line);
    return 0;
}
