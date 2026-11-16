// Rechner-Logik: Eingabezeile, Auswertung mit Punkt vor Strich und Klammern, Verlauf.
// SPDX-License-Identifier: GPL-3.0-or-later

#include "calc.h"

#include <math.h>
#include <stdio.h>
#include <string.h>

#define MAX_DIGITS 15
#define MAX_DEPTH 32

typedef struct
{
    const char* text;
    int position;
    int depth;
    const char* error;
} parser;

static double parse_sum(parser* p);

static char peek(const parser* p) { return p->text[p->position]; }

static int is_operator(char c) { return c == '+' || c == '-' || c == '*' || c == '/'; }

static void fail(parser* p, const char* message)
{
    if (p->error == NULL) p->error = message;
}

static double parse_number(parser* p)
{
    int digits = 0, dot = 0, decimals = 0;
    double mantissa = 0;
    while (1)
    {
        char c = peek(p);
        if (c >= '0' && c <= '9') { mantissa = mantissa * 10 + (c - '0'); digits++; decimals += dot; }
        else if (c == '.' && !dot) dot = 1;
        else break;
        p->position++;
    }
    if (digits == 0) { fail(p, "UNGUELTIGER AUSDRUCK"); return 0; }
    // Ein Exponent entsteht nur beim Weiterrechnen mit einem Ergebnis wie "1e+12"; getippt wird er nicht.
    int exponent = 0;
    if (peek(p) == 'e')
    {
        p->position++;
        int negative = peek(p) == '-';
        if (peek(p) == '-' || peek(p) == '+') p->position++;
        int exponent_digits = 0;
        while (peek(p) >= '0' && peek(p) <= '9' && exponent_digits < 4)
        {
            exponent = exponent * 10 + (peek(p) - '0');
            exponent_digits++;
            p->position++;
        }
        if (exponent_digits == 0) { fail(p, "UNGUELTIGER AUSDRUCK"); return 0; }
        if (negative) exponent = -exponent;
    }
    // Ganzzahlige Mantisse durch eine Zehnerpotenz: bei hoechstens 15 Ziffern korrekt gerundet und
    // unabhaengig von strtod und Gebietsschema.
    double scale = 1;
    for (int i = 0; i < decimals; i++) scale *= 10;
    double value = mantissa / scale;
    for (; exponent > 0; exponent--) value *= 10;
    for (; exponent < 0; exponent++) value /= 10;
    return value;
}

static double parse_primary(parser* p)
{
    if (peek(p) == '(')
    {
        p->position++;
        if (++p->depth > MAX_DEPTH) { fail(p, "ZU VIELE KLAMMERN"); return 0; }
        double value = parse_sum(p);
        p->depth--;
        // Fehlende schliessende Klammern am Ende ergaenzt der Rechner wie gaengige Taschenrechner.
        if (peek(p) == ')') p->position++;
        else if (peek(p) != 0) fail(p, "UNGUELTIGER AUSDRUCK");
        return value;
    }
    return parse_number(p);
}

static double parse_postfix(parser* p, int* percent)
{
    double value = parse_primary(p);
    while (peek(p) == '%') { p->position++; value /= 100; *percent = 1; }
    return value;
}

static double parse_unary(parser* p, int* percent)
{
    if (peek(p) == '-') { p->position++; return -parse_unary(p, percent); }
    if (peek(p) == '+') { p->position++; return parse_unary(p, percent); }
    return parse_postfix(p, percent);
}

// percent meldet, ob der Term genau ein Faktor mit "%" ist; nur dann gilt die Prozentregel bei + und -.
static double parse_product(parser* p, int* percent)
{
    *percent = 0;
    double value = parse_unary(p, percent);
    while (peek(p) == '*' || peek(p) == '/')
    {
        char op = p->text[p->position++];
        int ignored = 0;
        *percent = 0;
        double right = parse_unary(p, &ignored);
        if (op == '/')
        {
            if (right == 0) { fail(p, "DIVISION DURCH 0"); return 0; }
            value /= right;
        }
        else value *= right;
    }
    return value;
}

static double parse_sum(parser* p)
{
    int percent;
    double value = parse_product(p, &percent);
    while (peek(p) == '+' || peek(p) == '-')
    {
        char op = p->text[p->position++];
        double right = parse_product(p, &percent);
        // Wie bei ueblichen Taschenrechnern: "50+10%" sind 50 plus 10 % von 50.
        if (percent) right *= value;
        value = op == '+' ? value + right : value - right;
    }
    return value;
}

int calc_eval(const char* text, double* value, const char** message)
{
    parser p = { text, 0, 0, NULL };
    if (text[0] == 0) { *message = "LEER"; return 1; }
    double result = parse_sum(&p);
    if (p.error == NULL && peek(&p) != 0) fail(&p, "UNGUELTIGER AUSDRUCK");
    if (p.error == NULL && (isnan(result) || isinf(result) || fabs(result) >= 1e100)) fail(&p, "UEBERLAUF");
    if (p.error != NULL) { *message = p.error; return 1; }
    *value = result;
    return 0;
}

void calc_format(double value, char* target, int capacity)
{
    if (value == 0) value = 0;
    // Hoechstens 19 Zeichen (Vorzeichen, 12 Ziffern, Punkt, Exponent bis e-100), passt in CALC_MAX_TEXT.
    snprintf(target, (size_t)capacity, "%.12g", value);
}

void calc_reset(calc_state* state)
{
    memset(state, 0, sizeof(*state));
}

static int length_of(const calc_state* state) { return (int)strlen(state->input); }

static char last(const calc_state* state)
{
    int length = length_of(state);
    return length > 0 ? state->input[length - 1] : 0;
}

// Beginn der Zahl, die bei end endet: Ziffern, Punkt und ein Exponent wie in "1e-05".
static int number_begin(const char* text, int end)
{
    int i = end;
    while (i > 0)
    {
        char c = text[i - 1];
        if ((c >= '0' && c <= '9') || c == '.' || c == 'e') i--;
        else if ((c == '+' || c == '-') && i >= 2 && text[i - 2] == 'e') i--;
        else break;
    }
    return i;
}

static int number_start(const calc_state* state) { return number_begin(state->input, length_of(state)); }

static int append(calc_state* state, char c)
{
    int length = length_of(state);
    if (length >= CALC_MAX_INPUT) return 0;
    state->input[length] = c;
    state->input[length + 1] = 0;
    return 1;
}

static int open_parentheses(const calc_state* state)
{
    int open = 0;
    for (const char* c = state->input; *c; c++) open += *c == '(' ? 1 : *c == ')' ? -1 : 0;
    return open;
}

// Nach "=" oder einem Fehler: Ziffern beginnen neu, Operatoren rechnen mit dem Ergebnis weiter.
static void continue_after_result(calc_state* state, char c)
{
    if (!state->evaluated) return;
    state->evaluated = 0;
    int digit_like = (c >= '0' && c <= '9') || c == '.' || c == '(';
    if (digit_like || state->error) state->input[0] = 0;
    else snprintf(state->input, sizeof(state->input), "%s", state->result);
    state->error = 0;
    state->result[0] = 0;
}

int calc_input(calc_state* state, char c)
{
    if (c == 'x' || c == 'X') c = '*';
    else if (c == ':') c = '/';
    else if (c == ',') c = '.';
    int digit = c >= '0' && c <= '9';
    if (!digit && c != '.' && !is_operator(c) && c != '%' && c != '(' && c != ')') return 0;
    continue_after_result(state, c);
    char before = last(state);
    int start = number_start(state);
    int in_number = start < length_of(state);
    if (digit)
    {
        int digits = 0;
        for (int i = start; i < length_of(state); i++) digits += state->input[i] != '.';
        if (digits >= MAX_DIGITS || before == ')' || before == '%') return 0;
        // Eine fuehrende Null ersetzt die naechste Ziffer statt "007" zu bilden.
        if (in_number && length_of(state) - start == 1 && state->input[start] == '0') { state->input[start] = c; return 1; }
        return append(state, c);
    }
    if (c == '.')
    {
        if (before == ')' || before == '%') return 0;
        for (int i = start; i < length_of(state); i++) if (state->input[i] == '.') return 0;
        if (!in_number && !append(state, '0')) return 0;
        return append(state, '.');
    }
    if (c == '(')
    {
        if ((before >= '0' && before <= '9') || before == '.' || before == ')' || before == '%') return 0;
        return append(state, '(');
    }
    if (c == ')')
    {
        if (open_parentheses(state) <= 0 || is_operator(before) || before == '(' || before == 0) return 0;
        return append(state, ')');
    }
    if (c == '%')
    {
        if (!((before >= '0' && before <= '9') || before == ')')) return 0;
        return append(state, '%');
    }
    // Operator: am Anfang und nach "(" nur ein Vorzeichen-Minus; ein zweiter Operator ersetzt den ersten.
    if (before == 0 || before == '(') return c == '-' ? append(state, '-') : 0;
    if (before == '.') return 0;
    if (is_operator(before))
    {
        int length = length_of(state);
        if (length >= 2 && (state->input[length - 2] == '(' )) return 0;
        state->input[length - 1] = c;
        return 1;
    }
    return append(state, c);
}

void calc_backspace(calc_state* state)
{
    if (state->evaluated) { calc_clear(state); return; }
    int length = length_of(state);
    if (length > 0) state->input[length - 1] = 0;
}

void calc_clear_entry(calc_state* state)
{
    if (state->evaluated) { calc_clear(state); return; }
    int start = number_start(state);
    if (start < length_of(state)) state->input[start] = 0;
    else calc_backspace(state);
}

void calc_clear(calc_state* state)
{
    state->input[0] = 0;
    state->result[0] = 0;
    state->error = 0;
    state->evaluated = 0;
}

// Index der "(", die zur ")" an Position close gehoert, sonst -1.
static int matching_open(const char* text, int close)
{
    int depth = 0;
    for (int i = close; i >= 0; i--)
    {
        depth += text[i] == ')' ? 1 : text[i] == '(' ? -1 : 0;
        if (depth == 0) return i;
    }
    return -1;
}

void calc_negate(calc_state* state)
{
    if (state->evaluated && !state->error)
    {
        snprintf(state->input, sizeof(state->input), "%s", state->result);
        state->evaluated = 0;
        state->result[0] = 0;
    }
    int end = length_of(state);
    if (end == 0) return;
    int start;
    if (state->input[end - 1] == ')')
    {
        start = matching_open(state->input, end - 1);
        if (start < 0) return;
        // "(-x)" am Ende wird wieder zu "x"; das gilt fuer Zahlen und Klammerausdruecke.
        if (state->input[start + 1] == '-')
        {
            int inner = start + 2, inner_end = end - 1;
            int number = number_begin(state->input, inner_end) == inner && inner < inner_end;
            int group = state->input[inner] == '(' && matching_open(state->input, inner_end - 1) == inner;
            if (number || group)
            {
                memmove(state->input + start, state->input + inner, (size_t)(inner_end - inner));
                state->input[start + inner_end - inner] = 0;
                return;
            }
        }
    }
    else
    {
        start = number_start(state);
        if (start == end) return;
    }
    // "(-x)" statt einfachem Minus, damit "5*-3" und Punkt vor Strich eindeutig bleiben.
    if (end + 3 > CALC_MAX_INPUT) return;
    char tail[CALC_MAX_INPUT + 1];
    snprintf(tail, sizeof(tail), "%s", state->input + start);
    snprintf(state->input + start, sizeof(state->input) - (size_t)start, "(-%s)", tail);
}

void calc_evaluate(calc_state* state)
{
    if (state->evaluated || state->input[0] == 0) return;
    double value;
    const char* message;
    if (calc_eval(state->input, &value, &message) != 0)
    {
        snprintf(state->result, sizeof(state->result), "%s", message);
        state->error = 1;
    }
    else
    {
        calc_format(value, state->result, sizeof(state->result));
        state->error = 0;
        if (state->history_count == CALC_HISTORY)
        {
            memmove(state->history[0], state->history[1], sizeof(state->history[0]) * (CALC_HISTORY - 1));
            state->history_count--;
        }
        char entry[CALC_HISTORY_TEXT];
        snprintf(entry, sizeof(entry), "%s=%s", state->input, state->result);
        memcpy(state->history[state->history_count++], entry, sizeof(entry));
    }
    state->evaluated = 1;
}
