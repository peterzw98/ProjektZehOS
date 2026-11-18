#!/usr/bin/env python3
"""Gemeinsames Kanban-Board (GitHub Projects „ZehOS Board“) für alle Teams und Agenten.

Ein Aufgabenblock ist ein Issue mit dem Label `block`. Wahrheit über die Übernahme ist das Issue selbst (Team-Label und
Kommentar „[BOARD] übernommen …“); die Spalte auf dem Board zeigt den Stand. Braucht `gh` mit den Rechten repo und project.

  board.py list [--status S] [--team T] [--all]  offene Blöcke (Standard: ohne Erledigt)
  board.py new TITEL --body-file DATEI [--team T]  neuen Block anlegen (Spalte Offen, mit --team gleich zugeteilt)
  board.py claim NR TEAM AGENT BRANCH              Block übernehmen; Exit 3, wenn ihn schon jemand anderes hat
  board.py status NR SPALTE                         Spalte setzen: Offen, Übernommen, In Arbeit, Review/PR, Erledigt
  board.py release NR TEAM [GRUND]                  eigenen Block wieder freigeben (Team-Label weg, Spalte Offen)

Inhaber ist die älteste noch gültige Übernahme, deren Team-Label gesetzt ist. „[BOARD] freigegeben team=X“ beendet die
Übernahmen von X, „[BOARD] zurückgezogen team=X agent=Y“ nur die eine von Y (Verlierer bei gleichzeitiger Übernahme).
"""
import argparse
import json
import re
import subprocess
import sys
import time

REPO = "peterzw98/ProjektZehOS"
OWNER, NUMBER = "peterzw98", 1
COLUMNS = ("Offen", "Übernommen", "In Arbeit", "Review/PR", "Erledigt")
CLAIM, FREE, WITHDRAW = "[BOARD] übernommen", "[BOARD] freigegeben", "[BOARD] zurückgezogen"
TOKEN = re.compile(r"[A-Za-z0-9._/+-]+")   # Team, Agent und Branch stehen als key=wert im Kommentar


def gh(*args, data=None, retry=True):
    # GitHub antwortet gelegentlich mit vorübergehenden Fehlern („Something went wrong“, 502); zwei Wiederholungen.
    # Nicht bei Aufrufen, die doppelt ausgeführt Schaden anrichten (issue create): Dort ist die Anfrage oft trotzdem angekommen.
    for attempt in range(3 if retry else 1):
        r = subprocess.run(["gh", *args], capture_output=True, text=True, input=data)
        if r.returncode == 0:
            return r.stdout
        transient = any(t in r.stderr for t in ("Something went wrong", "502", "503", "504", "timeout"))
        if not transient or not retry or attempt == 2:
            sys.exit(f"board: gh {' '.join(args[:3])} …: {r.stderr.strip()[:300]}")
        time.sleep(3 * (attempt + 1))


def graphql(query, **variables):
    args = ["api", "graphql", "-f", f"query={query}"]
    for k, v in variables.items():
        args += ["-F" if isinstance(v, int) else "-f", f"{k}={v}"]
    return json.loads(gh(*args))["data"]


def project():
    """Projekt-ID, ID des Status-Felds und Option-IDs je Spalte."""
    d = graphql("""query($o:String!,$n:Int!){user(login:$o){projectV2(number:$n){id
        field(name:"Status"){... on ProjectV2SingleSelectField{id options{id name}}}}}}""", o=OWNER, n=NUMBER)
    p = d["user"]["projectV2"]
    return p["id"], p["field"]["id"], {o["name"]: o["id"] for o in p["field"]["options"]}


def item_for(issue_id, pid):
    """Board-Eintrag des Issues; fehlt er, wird das Issue aufs Board gelegt."""
    d = graphql("""query($i:ID!){node(id:$i){... on Issue{projectItems(first:20){nodes{id project{id}}}}}}""", i=issue_id)
    for n in d["node"]["projectItems"]["nodes"]:
        if n["project"]["id"] == pid:
            return n["id"]
    d = graphql("""mutation($p:ID!,$c:ID!){addProjectV2ItemById(input:{projectId:$p,contentId:$c}){item{id}}}""",
                p=pid, c=issue_id)
    return d["addProjectV2ItemById"]["item"]["id"]


def set_status(number, column):
    if column not in COLUMNS:
        sys.exit(f"board: unbekannte Spalte {column!r}; erlaubt: {', '.join(COLUMNS)}")
    pid, fid, opts = project()
    if column not in opts:
        sys.exit(f"board: Spalte {column!r} fehlt auf dem Board (vorhanden: {', '.join(opts)})")
    issue = json.loads(gh("issue", "view", str(number), "-R", REPO, "--json", "id"))
    item = item_for(issue["id"], pid)
    graphql("""mutation($p:ID!,$i:ID!,$f:ID!,$o:String!){updateProjectV2ItemFieldValue(input:{projectId:$p,itemId:$i,
        fieldId:$f,value:{singleSelectOptionId:$o}}){projectV2Item{id}}}""", p=pid, i=item, f=fid, o=opts[column])


def issue(number):
    return json.loads(gh("issue", "view", str(number), "-R", REPO, "--json", "id,title,state,labels,comments"))


def teams_of(data):
    return [l["name"][5:] for l in data["labels"] if l["name"].startswith("team:")]


def fields(body, prefix):
    return dict(p.split("=", 1) for p in body[len(prefix):].split() if "=" in p)


def claims(data):
    """Gültige Übernahmen, älteste zuerst (GitHub liefert Kommentare in Zeitfolge). Nur Teams mit gesetztem Label zählen:
    Wer ein Label von Hand entfernt, hinterlässt sonst eine Übernahme, an der jedes neue Team scheitert."""
    out = []
    for c in data["comments"]:
        body = c["body"].strip()
        if body.startswith(FREE):
            team = fields(body, FREE).get("team")
            out = [x for x in out if team and x.get("team") != team]   # ohne team= (alte Form): alle
        elif body.startswith(WITHDRAW):
            f = fields(body, WITHDRAW)
            out = [x for x in out if (x.get("team"), x.get("agent")) != (f.get("team"), f.get("agent"))]
        elif body.startswith(CLAIM):
            out.append(fields(body, CLAIM))
    labelled = set(teams_of(data))
    return [x for x in out if x.get("team") in labelled]


def owner_of(data):
    c = claims(data)
    return c[0] if c else None


def comment(number, body):
    gh("issue", "comment", str(number), "-R", REPO, "--body", body)


def cmd_claim(a):
    team = a.team.lower()
    for v in (team, a.agent, a.branch):
        if not TOKEN.fullmatch(v):
            sys.exit(f"board: {v!r} enthält unerlaubte Zeichen (erlaubt: Buchstaben, Ziffern, . _ / + -)")
    me = (team, a.agent)
    data = issue(a.number)
    if data["state"] != "OPEN":
        sys.exit(f"board: #{a.number} ist geschlossen")
    other = [t for t in teams_of(data) if t != team]
    owner = owner_of(data)
    if other or (owner and (owner.get("team"), owner.get("agent")) != me):
        who = f"{owner.get('team')} ({owner.get('agent')})" if owner else ", ".join(other)
        print(f"board: #{a.number} hat schon {who}; nicht übernehmen", file=sys.stderr)
        sys.exit(3)
    if owner:   # dieselbe Übernahme noch einmal: nichts zu tun
        set_status(a.number, "Übernommen")
        print(f"#{a.number} gehört schon {team} ({a.agent})")
        return
    gh("issue", "edit", str(a.number), "-R", REPO, "--add-label", f"team:{team}")
    try:
        comment(a.number, f"{CLAIM} team={team} agent={a.agent} branch={a.branch}")
    except SystemExit:
        # Ohne Kommentar bliebe ein Label ohne Inhaber stehen und sperrte den Block für andere Teams
        gh("issue", "edit", str(a.number), "-R", REPO, "--remove-label", f"team:{team}")
        raise
    # Gleichzeitige Übernahmen: Der frühere Kommentar gilt. Den eigenen Kommentar abwarten, falls GitHub ihn noch nicht liefert.
    for attempt in range(5):
        c = claims(issue(a.number))
        mine = [x for x in c if (x.get("team"), x.get("agent")) == me]
        if mine:
            break
        time.sleep(2)
    else:
        sys.exit(f"board: eigene Übernahme von #{a.number} nicht lesbar; mit 'board.py list' prüfen und erneut claimen")
    first = c[0]
    if (first.get("team"), first.get("agent")) != me:
        comment(a.number, f"{WITHDRAW} team={team} agent={a.agent} (zeitgleich, {first.get('agent')} war früher)")
        if first.get("team") != team:
            gh("issue", "edit", str(a.number), "-R", REPO, "--remove-label", f"team:{team}")
        print(f"board: #{a.number} hat {first.get('team')} ({first.get('agent')}) zuerst übernommen", file=sys.stderr)
        sys.exit(3)
    set_status(a.number, "Übernommen")
    print(f"#{a.number} übernommen von {team} ({a.agent}, Branch {a.branch})")


def cmd_release(a):
    team = a.team.lower()
    data = issue(a.number)
    if team not in teams_of(data):
        sys.exit(f"board: #{a.number} gehört nicht Team {team}; nur das eigene Team gibt frei")
    gh("issue", "edit", str(a.number), "-R", REPO, "--remove-label", f"team:{team}")
    comment(a.number, f"{FREE} team={team} {a.reason}".strip())
    if not [t for t in teams_of(data) if t != team]:
        set_status(a.number, "Offen")
    print(f"#{a.number} freigegeben")


def cmd_new(a):
    args = ["issue", "create", "-R", REPO, "--title", a.title, "--body-file", a.body_file, "--label", "block"]
    if a.team:
        args += ["--label", f"team:{a.team.lower()}"]
    # Nicht wiederholen: Ein zweiter Versuch nach einem Zeitfehler legte den Block oft doppelt an
    # Liste statt Suche: Der Suchindex kennt gerade angelegte Issues noch nicht
    found = json.loads(gh("issue", "list", "-R", REPO, "--label", "block", "--state", "open", "--limit", "1000",
                          "--json", "number,title,url"))
    same = [i for i in found if i["title"] == a.title]
    if same:
        sys.exit(f"board: Block mit diesem Titel gibt es schon: {same[0]['url']}")
    url = gh(*args, retry=False).strip()
    number = int(url.rsplit("/", 1)[1])
    set_status(number, "Offen")
    print(url)


def board_items():
    """Alle Einträge des Boards, seitenweise (GitHub liefert höchstens 100 je Abfrage)."""
    items, after = [], None
    while True:
        q = """query($o:String!,$n:Int!,$a:String){user(login:$o){projectV2(number:$n){items(first:100,after:$a){
            pageInfo{hasNextPage endCursor} nodes{
            fieldValueByName(name:"Status"){... on ProjectV2ItemFieldSingleSelectValue{name}}
            content{... on Issue{number title state labels(first:20){nodes{name}}}
                    ... on PullRequest{number title state labels(first:20){nodes{name}}}}}}}}}"""
        d = graphql(q, o=OWNER, n=NUMBER, **({"a": after} if after else {}))["user"]["projectV2"]["items"]
        items += d["nodes"]
        if not d["pageInfo"]["hasNextPage"]:
            return items
        after = d["pageInfo"]["endCursor"]


def cmd_list(a):
    rows = []
    for n in board_items():
        c = n["content"] or {}
        if "number" not in c:
            continue
        status = (n["fieldValueByName"] or {}).get("name", "Offen")
        teams = [l["name"][5:] for l in c["labels"]["nodes"] if l["name"].startswith("team:")]
        if not a.all and status == "Erledigt":
            continue
        if a.status and status != a.status:
            continue
        if a.team and a.team.lower() not in teams:
            continue
        rows.append((COLUMNS.index(status) if status in COLUMNS else 0, c["number"], status, ",".join(teams) or "-",
                     c["title"]))
    for _, number, status, teams, title in sorted(rows):
        print(f"#{number:<5} {status:<11} {teams:<8} {title}")


def main():
    p = argparse.ArgumentParser(description="Gemeinsames Kanban-Board (ZehOS Board)")
    sub = p.add_subparsers(dest="cmd", required=True)
    s = sub.add_parser("list"); s.add_argument("--status"); s.add_argument("--team"); s.add_argument("--all", action="store_true")
    s = sub.add_parser("new"); s.add_argument("title"); s.add_argument("--body-file", required=True); s.add_argument("--team")
    s = sub.add_parser("claim"); s.add_argument("number", type=int); s.add_argument("team"); s.add_argument("agent"); s.add_argument("branch")
    s = sub.add_parser("status"); s.add_argument("number", type=int); s.add_argument("column")
    s = sub.add_parser("release"); s.add_argument("number", type=int); s.add_argument("team"); s.add_argument("reason", nargs="?", default="")
    a = p.parse_args()
    if a.cmd == "status":
        set_status(a.number, a.column)
        print(f"#{a.number} → {a.column}")
    else:
        {"list": cmd_list, "new": cmd_new, "claim": cmd_claim, "release": cmd_release}[a.cmd](a)


if __name__ == "__main__":
    main()
