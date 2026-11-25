/* Treiber für toybox: legen im (leeren) Arbeitsverzeichnis feste Eingaben an und
 * starten dann das toybox-Programm per execv. toybox liegt relativ zum Treiber in
 * tb/ (Pfad aus argv[0]); die Ausgabe des Treibers selbst ist leer, es zählt die von toybox. */
#ifndef LINUXABI_TB_COMMON_H
#define LINUXABI_TB_COMMON_H
#include <fcntl.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

static void tb_path(const char *self, const char *applet, char *buf, size_t n)
{
    const char *slash = strrchr(self, '/');
    int dir = slash ? (int)(slash - self) : 0;
    if (slash) snprintf(buf, n, "%.*s/tb/%s", dir, self, applet);
    else snprintf(buf, n, "tb/%s", applet);
}

__attribute__((unused)) static void tb_file(const char *name, const char *content)
{
    int fd = open(name, O_WRONLY | O_CREAT | O_TRUNC, 0644);
    if (fd < 0 || write(fd, content, strlen(content)) < 0) { perror(name); exit(2); }
    close(fd);
}
#endif
