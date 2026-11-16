// Hosttest der Rechner-Logik: gcc -I../src ../src/calc.c calc_test.c -lm && ./a.out
// SPDX-License-Identifier: GPL-3.0-or-later

#include <stdio.h>
#include <string.h>

#include "calc.h"

static int failures;

static void expect_eval(const char* text, const char* expected)
{
    double value;
    const char* message;
    char shown[CALC_MAX_TEXT];
    if (calc_eval(text, &value, &message) != 0) snprintf(shown, sizeof(shown), "FEHLER:%s", message);
    else calc_format(value, shown, sizeof(shown));
    if (strcmp(shown, expected) != 0)
    {
        printf("FAIL eval '%s': '%s' statt '%s'\n", text, shown, expected);
        failures++;
    }
}

// Tippt Zeichen ein; '=' wertet aus, 'N' ist +/-, 'B' Ruecktaste, 'E' CE, 'C' C.
static void type(calc_state* state, const char* keys)
{
    for (const char* k = keys; *k; k++)
    {
        if (*k == '=') calc_evaluate(state);
        else if (*k == 'N') calc_negate(state);
        else if (*k == 'B') calc_backspace(state);
        else if (*k == 'E') calc_clear_entry(state);
        else if (*k == 'C') calc_clear(state);
        else calc_input(state, *k);
    }
}

static void expect_keys(const char* keys, const char* input, const char* result)
{
    calc_state state;
    calc_reset(&state);
    type(&state, keys);
    if (strcmp(state.input, input) != 0 || strcmp(state.result, result) != 0)
    {
        printf("FAIL keys '%s': Eingabe '%s' Ergebnis '%s' statt '%s' / '%s'\n", keys, state.input, state.result, input, result);
        failures++;
    }
}

int main(void)
{
    // Punkt vor Strich, Klammern, Vorzeichen, Prozent.
    expect_eval("1+2*3", "7");
    expect_eval("(1+2)*3", "9");
    expect_eval("10-4-3", "3");
    expect_eval("100/4/5", "5");
    expect_eval("-3*-2", "6");
    expect_eval("2*(-3)", "-6");
    expect_eval("50%", "0.5");
    expect_eval("200*10%", "20");
    expect_eval("((2+3)*(4-1))/5", "3");
    expect_eval("(1+2", "3");
    // Rundung: Binaerfehler verschwinden bei 12 signifikanten Stellen.
    expect_eval("0.1+0.2", "0.3");
    expect_eval("1/3", "0.333333333333");
    expect_eval("2/3", "0.666666666667");
    expect_eval("0-0", "0");
    expect_eval("-0*5", "0");
    expect_eval("123456789012*1000", "1.23456789012e+14");
    expect_eval("0.000001*0.000001", "1e-12");
    // Fehlerzustaende.
    expect_eval("1/0", "FEHLER:DIVISION DURCH 0");
    expect_eval("1/(2-2)", "FEHLER:DIVISION DURCH 0");
    expect_eval("1e5", "FEHLER:UNGUELTIGER AUSDRUCK");
    expect_eval("1+", "FEHLER:UNGUELTIGER AUSDRUCK");
    expect_eval("()", "FEHLER:UNGUELTIGER AUSDRUCK");
    expect_eval("1)", "FEHLER:UNGUELTIGER AUSDRUCK");
    expect_eval("", "FEHLER:LEER");
    expect_eval("999999999999999*999999999999999*999999999999999*999999999999999*999999999999999*999999999999999*999999999999999",
                "FEHLER:UEBERLAUF");
    expect_eval("((((((((((((((((((((((((((((((((((1))))))))))))))))))))))))))))))))))", "FEHLER:ZU VIELE KLAMMERN");

    // Eingabezeile.
    expect_keys("12+3=", "12+3", "15");
    expect_keys("12+3=*2=", "15*2", "30");
    expect_keys("12+3=4", "4", "");
    expect_keys("007", "7", "");
    expect_keys(".5", "0.5", "");
    expect_keys("1.2.3", "1.23", "");
    expect_keys("5+*", "5*", "");
    expect_keys("*5", "5", "");
    expect_keys("-5", "-5", "");
    expect_keys("5N", "(-5)", "");
    expect_keys("5NN", "5", "");
    expect_keys("3*5N=", "3*(-5)", "-15");
    expect_keys("12+34E", "12+", "");
    expect_keys("12+34B", "12+3", "");
    expect_keys("12+34C", "", "");
    expect_keys("1/0=", "1/0", "DIVISION DURCH 0");
    // Nach einem Fehler beginnt eine neue Rechnung; ein fuehrendes "+" wird verworfen.
    expect_keys("1/0=+2", "2", "");
    expect_keys("2(", "2", "");
    expect_keys("(2))", "(2)", "");
    expect_keys("2)", "2", "");
    expect_keys("5%3", "5%", "");
    expect_keys("1234567890123456", "123456789012345", "");
    // Eingabe ist auf CALC_MAX_INPUT begrenzt.
    calc_state state;
    calc_reset(&state);
    for (int i = 0; i < 200; i++) calc_input(&state, i % 2 ? '+' : '1');
    if (strlen(state.input) > CALC_MAX_INPUT) { printf("FAIL Eingabegrenze %zu\n", strlen(state.input)); failures++; }
    // Verlauf haelt die letzten vier Rechnungen, aelteste zuerst.
    calc_reset(&state);
    type(&state, "1+1=C2+2=C3+3=C4+4=C5+5=");
    if (state.history_count != CALC_HISTORY || strcmp(state.history[0], "2+2=4") != 0 || strcmp(state.history[3], "5+5=10") != 0)
    {
        printf("FAIL Verlauf: %d '%s' .. '%s'\n", state.history_count, state.history[0], state.history[CALC_HISTORY - 1]);
        failures++;
    }
    // Fehler landen nicht im Verlauf.
    calc_reset(&state);
    type(&state, "1/0=");
    if (state.history_count != 0) { printf("FAIL Fehler im Verlauf\n"); failures++; }

    printf(failures ? "%d Fehler\n" : "alle Tests bestanden\n", failures);
    return failures != 0;
}
