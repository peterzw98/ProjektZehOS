/* T3: SIGSEGV per kill an sich selbst ohne Handler (Standardbehandlung): signal 11. */
#include <signal.h>
#include <stdio.h>
#include <unistd.h>
int main(void) { puts("vor kill"); fflush(stdout); kill(getpid(), SIGSEGV); puts("unerreichbar"); return 0; }
