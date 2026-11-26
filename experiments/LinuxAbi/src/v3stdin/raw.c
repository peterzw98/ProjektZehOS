/* V3 raw: Zeileneditor wie LibLine. ICANON und ECHO aus (ISIG und ICRNL bleiben), Frage nach der Cursorposition (ESC [ 6 n),
   dann jede Taste einzeln lesen und die Zeile selbst neu zeichnen (\r, ESC [ K, ESC [ n D). Dazu Prozessgruppe und Sitzung:
   Gruppen- und Sitzungsfuehrer wie in einem neuen pty (setpgid und setsid EPERM). */
#include <errno.h>
#include <stdio.h>
#include <string.h>
#include <termios.h>
#include <unistd.h>
static char buf[128];
static int len, cur;
static void redraw(void)
{
    char out[256];
    int n = snprintf(out, sizeof out, "\r\033[K> %.*s", len, buf);
    if (cur < len) n += snprintf(out + n, sizeof out - n, "\033[%dD", len - cur);
    write(1, out, n);
}
static int key(void)
{
    unsigned char c;
    return read(0, &c, 1) == 1 ? c : -1;
}
int main(void)
{
    struct termios t, raw;
    int reads = 0, row = 0, col = 0, c;
    if (tcgetattr(0, &t) != 0) { printf("tcgetattr?\n"); return 4; }
    errno = 0;
    printf("pg %d%d%d%d\n", getpgrp() == getpid(), getpgid(0) == getpid(), setpgid(0, 0) == -1 && errno == EPERM, getsid(0) == getpid());
    errno = 0;
    printf("setsid %d\n", setsid() == -1 && errno == EPERM);
    raw = t;
    raw.c_lflag &= ~(ICANON | ECHO);
    if (tcsetattr(0, TCSANOW, &raw) != 0) { printf("tcsetattr?\n"); return 4; }
    write(2, "\033[6n", 4);
    if (key() != 033 || key() != '[') { printf("dsr?\n"); return 6; }
    while ((c = key()) >= '0' && c <= '9') row = row * 10 + c - '0';
    if (c != ';') { printf("dsr;?\n"); return 6; }
    while ((c = key()) >= '0' && c <= '9') col = col * 10 + c - '0';
    printf("dsr %d %d\n", row > 1 && c == 'R', col);
    redraw();
    for (;;) {
        c = key();
        reads++;
        if (c == 033) {
            /* Pfeil: ESC [ C/D; ein einzelnes Esc (die naechste Taste ist kein '[') wird uebergangen. */
            c = key();
            reads++;
            if (c == '[') {
                c = key();
                reads++;
                if (c == 'D' && cur > 0) cur--;
                if (c == 'C' && cur < len) cur++;
                redraw();
                continue;
            }
        }
        if (c < 0 || c == '\n') break;
        if (c == 0x7f) {
            if (cur > 0) { memmove(buf + cur - 1, buf + cur, len - cur); cur--; len--; }
        } else if (c >= ' ' && len < (int)sizeof buf - 1) {
            memmove(buf + cur + 1, buf + cur, len - cur);
            buf[cur++] = (char)c;
            len++;
        }
        redraw();
    }
    write(1, "\n", 1);
    buf[len] = 0;
    tcsetattr(0, TCSANOW, &t);
    printf("zeile [%s] einzeln %d\n", buf, reads > 8);
    return 0;
}
