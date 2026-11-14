#pragma once
/* floor/floorf liefert Cosmos 3.0.94; die uebrigen Funktionen stehen in libc.c. */
double fabs(double value);
float fabsf(float value);
double ceil(double value);
float ceilf(float value);
double floor(double value);
float floorf(float value);
double trunc(double value);
float truncf(float value);
double rint(double value);
float rintf(float value);
double sqrt(double value);
float sqrtf(float value);
double copysign(double magnitude, double sign);
float copysignf(float magnitude, float sign);
#define INFINITY __builtin_inff()
#define NAN __builtin_nanf("")
#define signbit(x) __builtin_signbit(x)
#define isnan(x) __builtin_isnan(x)
#define isinf(x) __builtin_isinf(x)
