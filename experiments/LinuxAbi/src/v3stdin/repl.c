/* V3 stdin: Zeilen-REPL; poll auf fd 0 ohne Frist, dann fgets (musl liest mit readv). quit -> exit 3, Strg+D -> exit 0. */
#include <stdio.h>
#include <string.h>
#include <poll.h>
int main(void)
{
    char line[256];
    printf("Echo-REPL\n");
    for (;;) {
        printf("> ");
        fflush(stdout);
        struct pollfd p = { 0, POLLIN, 0 };
        if (poll(&p, 1, -1) != 1) { printf("poll?\n"); return 4; }
        if (!fgets(line, sizeof line, stdin)) { printf("EOF\n"); return 0; }
        line[strcspn(line, "\n")] = 0;
        if (!strcmp(line, "quit")) { printf("tschuess\n"); return 3; }
        printf("= %s (%zu)\n", line, strlen(line));
        fflush(stdout);
    }
}
