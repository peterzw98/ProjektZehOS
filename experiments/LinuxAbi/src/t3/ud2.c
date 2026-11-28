/* T3: ungueltiger Befehl (#UD) ohne Handler: signal 4. */
#include <stdio.h>
int main(void) { puts("vor ud2"); fflush(stdout); __asm__ volatile("ud2"); puts("unerreichbar"); return 0; }
