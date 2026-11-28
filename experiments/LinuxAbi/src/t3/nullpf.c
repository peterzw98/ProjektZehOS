/* T3: Seitenfehler (#PF) auf Adresse 0 ohne Handler: Prozess endet mit signal 11, der Kernel laeuft weiter. */
#include <stdio.h>
int main(void) { puts("vor dem Fehler"); fflush(stdout); *(volatile int *)0 = 1; puts("unerreichbar"); return 0; }
