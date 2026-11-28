/* T3: Ganzzahldivision durch 0 (#DE) ohne Handler: signal 8. Das div steht im Assembler, damit der Uebersetzer es nicht wegoptimiert. */
#include <stdio.h>
int main(void)
{
    puts("vor div0"); fflush(stdout);
    __asm__ volatile("xor %%edx,%%edx; mov $1,%%eax; xor %%ecx,%%ecx; div %%ecx" ::: "eax", "ecx", "edx");
    puts("unerreichbar");
    return 0;
}
