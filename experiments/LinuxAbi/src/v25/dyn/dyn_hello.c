/* V25: dynamisch gelinktes musl-Programm. Prueft, dass der Kern den Interpreter (PT_INTERP) laedt und
   der Hilfsvektor AT_BASE/AT_ENTRY/AT_PHDR liefert; dlopen bildet eine weitere Bibliothek per mmap ab. */
#include <dlfcn.h>
#include <link.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/auxv.h>

extern void _start(void);

static int objects(struct dl_phdr_info *info, size_t size, void *data)
{
    (void)info;
    (void)size;
    ++*(int *)data;
    return 0;
}

int main(int argc, char **argv)
{
    printf("hello dynamic argc=%d argv0=%s\n", argc, argc > 0 ? argv[0] : "-");
    printf("at_base %s\n", getauxval(AT_BASE) != 0 ? "gesetzt" : "null");
    printf("at_entry %s\n", getauxval(AT_ENTRY) == (unsigned long)&_start ? "ok" : "falsch");
    printf("at_phdr %s\n", getauxval(AT_PHDR) != 0 && getauxval(AT_PHNUM) > 0 ? "ok" : "falsch");

    char *big = malloc(1 << 20);
    if (big == NULL) {
        return 2;
    }
    memset(big, 'x', 1 << 20);
    printf("malloc ok %c\n", big[12345]);
    free(big);

    int before = 0;
    dl_iterate_phdr(objects, &before);
    void *h = dlopen("libgcc_s.so.1", RTLD_NOW);
    if (h == NULL) {
        printf("dlopen fehler %s\n", dlerror());
        return 3;
    }
    printf("dlsym %s\n", dlsym(h, "_Unwind_Backtrace") != NULL ? "ok" : "fehlt");
    int after = 0;
    dl_iterate_phdr(objects, &after);
    /* Linux zaehlt den vDSO mit, ZehOS hat keinen: nur der Zuwachs durch dlopen ist vergleichbar. */
    printf("objekte +%d\n", after - before);
    return 0;
}
