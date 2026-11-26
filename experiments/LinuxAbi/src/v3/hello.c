/* Ladybird V3, Stufe (a): das kleinste Programm, das im ZehOS-Terminal Text ausgibt und mit 0 endet (stdio von musl:
 * ioctl/TIOCGWINSZ auf stdout, writev, exit_group). */
#include <stdio.h>

int main(void)
{
    printf("Hallo aus dem Ring 3!\n");
    return 0;
}
