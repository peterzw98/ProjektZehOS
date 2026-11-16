// Rechner-Logik ohne Plattformabhaengigkeit; die Hosttests binden sie direkt ein.
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef CALC_H
#define CALC_H

#define CALC_MAX_INPUT 64
// Ergebnisse (%.12g) und Fehlertexte sind kuerzer als die Eingabezeile, damit Weiterrechnen immer passt.
#define CALC_MAX_TEXT 32
#define CALC_HISTORY_TEXT (CALC_MAX_INPUT + CALC_MAX_TEXT + 2)
#define CALC_HISTORY 4

typedef struct
{
    char input[CALC_MAX_INPUT + 1];
    // Ergebnis oder Fehlermeldung der letzten Auswertung; leer, solange nichts ausgewertet wurde.
    char result[CALC_MAX_TEXT];
    int error;
    // Nach "=" beginnt eine Ziffer eine neue Rechnung, ein Operator rechnet mit dem Ergebnis weiter.
    int evaluated;
    char history[CALC_HISTORY][CALC_HISTORY_TEXT];
    int history_count;
} calc_state;

void calc_reset(calc_state* state);
// Nimmt ein Eingabezeichen an (Ziffern, . + - * / % ( ) sowie x : , als Ersatz); 0, wenn es verworfen wurde.
int calc_input(calc_state* state, char c);
void calc_backspace(calc_state* state);
// CE: entfernt die zuletzt begonnene Zahl, C: setzt alles ausser dem Verlauf zurueck.
void calc_clear_entry(calc_state* state);
void calc_clear(calc_state* state);
// Wechselt das Vorzeichen der letzten Zahl.
void calc_negate(calc_state* state);
void calc_evaluate(calc_state* state);

// Wertet einen Ausdruck aus; 0 bei Erfolg, sonst Fehlertext in message.
int calc_eval(const char* text, double* value, const char** message);
// Formatiert mit hoechstens 12 signifikanten Stellen; -0 wird 0.
void calc_format(double value, char* target, int capacity);

#endif
