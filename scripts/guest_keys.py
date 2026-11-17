"""Zeichen -> QEMU-Tastenname fuer das deutsche Tastaturlayout des Gasts (DEStandardLayout).

QMP und der Monitorbefehl sendkey senden physische US-Tastenpositionen; im Gast sind y und z vertauscht und die
Satzzeichen liegen anders. Alle Skripte, die Text in den Gast tippen (Qmp in test-wasm-doom-desktop.py, damit auch
test-netsurf-desktop.py und test-storage-data-disk.py, sowie Monitor in test-network.py), verwenden diese eine Tabelle.
"""

# Mit Umschalttaste: Zeichen -> Tastenposition (US-Name).
SHIFTED = {":": "dot", ";": "comma", "/": "7", "_": "slash", "=": "0", "?": "minus", "\"": "2", "&": "6",
           "%": "5", "$": "4", "(": "8", ")": "9", "*": "bracket_right"}
# Ohne Umschalttaste, wo sich Zeichen und Position unterscheiden.
PLAIN = {" ": "spc", ".": "dot", ",": "comma", "-": "slash", "+": "bracket_right", "#": "backslash", "y": "z", "z": "y"}


def key_for(character: str) -> tuple[bool, str]:
    """(Umschalttaste noetig, Tastenname) fuer ein Zeichen; unbekannte Satzzeichen (etwa @, ~, die AltGr brauchen) sind ein Fehler statt einer falschen Taste."""
    if len(character) != 1:
        raise ValueError(f"Ein einzelnes Zeichen erwartet: {character!r}")
    if character in SHIFTED:
        return True, SHIFTED[character]
    if character.isupper():
        return True, PLAIN.get(character.lower(), character.lower())
    if character.isalnum() or character in PLAIN:
        return False, PLAIN.get(character, character)
    raise ValueError(f"Zeichen nicht abbildbar (deutsches Layout): {character!r}")
