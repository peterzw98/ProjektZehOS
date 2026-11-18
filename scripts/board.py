#!/usr/bin/env python3
"""Gemeinsames Kanban-Board (GitHub Projects „ZehOS Board“) für alle Teams und Agenten.

Ein Aufgabenblock ist ein Issue mit dem Label `block`. Wahrheit über die Übernahme ist das Issue selbst (Team-Label und
Kommentar „[BOARD] übernommen …“); die Spalte auf dem Board zeigt den Stand. Braucht `gh` mit den Rechten repo und project.

  board.py list [--status S] [--team T] [--all]  offene Blöcke (Standard: ohne Erledigt)
  board.py new TITEL --body-file DATEI [--team T]  neuen Block anlegen (Spalte Offen, mit --team gleich zugeteilt)
  board.py claim NR TEAM AGENT BRANCH              Block übernehmen; Exit 3, wenn ein anderes Team ihn schon hat
  board.py status NR SPALTE                         Spalte setzen: Offen, Übernommen, In Arbeit, Review/PR, Erledigt
  board.py release NR [GRUND]                       Block wieder freigeben (Team-Label weg, Spalte Offen)
"""
import argparse
import json
import subprocess
import sys
import time

REPO = "peterzw98/ProjektZehOS"
OWNER, NUMBER = "peterzw98", 1
COLUMNS = ("Offen", "Übernommen", "In Arbeit", "Review/PR", "Erledigt")
CLAIM, FREE = "[BOARD] übernommen", "[BOARD] freigegeben"


def gh(*args, data=None):
    # GitHub antwortet gelegentlich mit vorübergehenden Fehlern („Something went wrong“, 502); zwei Wiederholungen
    for attempt in range(3):
        r = subprocess.run(["gh", *args], capture_output=True, text=True, input=data)
        if r.returncode == 0:
            return r.stdout
        transient = any(t in r.stderr for t in ("Something went wrong", "502", "503", "504", "timeout"))
        if not transient or attempt == 2:
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
    issue = json.loads(gh("issue", "view", str(number), "-R", REPO, "--json", "id"))
    item = item_for(issue["id"], pid)
    graphql("""mutation($p:ID!,$i:ID!,$f:ID!,$o:String!){updateProjectV2ItemFieldValue(input:{projectId:$p,itemId:$i,
        fieldId:$f,value:{singleSelectOptionId:$o}}){projectV2Item{id}}}""", p=pid, i=item, f=fid, o=opts[column])


def issue(number):
    return json.loads(gh("issue", "view", str(number), "-R", REPO, "--json", "id,title,state,labels,comments"))


def teams_of(data):
    return [l["name"][5:] for l in data["labels"] if l["name"].startswith("team:")]


def current_claims(data):
    """Übernahmen seit der letzten Freigabe, älteste zuerst (GitHub liefert Kommentare in Zeitfolge)."""
    claims = []
    for c in data["comments"]:
        body = c["body"].strip()
        if body.startswith(FREE):
            claims = []
        elif body.startswith(CLAIM):
            fields = dict(p.split("=", 1) for p in body[len(CLAIM):].split() if "=" in p)
            claims.append(fields)
    return claims


def cmd_claim(a):
    team = a.team.lower()
    data = issue(a.number)
    if data["state"] != "OPEN":
        sys.exit(f"board: #{a.number} ist geschlossen")
    other = [t for t in teams_of(data) if t != team]
    if other:
        print(f"board: #{a.number} hat schon Team {', '.join(other)}; nicht übernehmen", file=sys.stderr)
        sys.exit(3)
    gh("issue", "edit", str(a.number), "-R", REPO, "--add-label", f"team:{team}")
    gh("issue", "comment", str(a.number), "-R", REPO, "--body",
       f"{CLAIM} team={team} agent={a.agent} branch={a.branch}")
    # Haben zwei Teams gleichzeitig übernommen, gilt der frühere Kommentar; der spätere zieht sich zurück
    first = current_claims(issue(a.number))[0]
    if first.get("team") != team:
        gh("issue", "edit", str(a.number), "-R", REPO, "--remove-label", f"team:{team}")
        gh("issue", "comment", str(a.number), "-R", REPO, "--body",
           f"{FREE} team={team}: zeitgleich übernommen, Team {first.get('team')} war früher")
        print(f"board: #{a.number} hat Team {first.get('team')} zuerst übernommen", file=sys.stderr)
        sys.exit(3)
    set_status(a.number, "Übernommen")
    print(f"#{a.number} übernommen von {team} ({a.agent}, Branch {a.branch})")


def cmd_release(a):
    data = issue(a.number)
    for t in teams_of(data):
        gh("issue", "edit", str(a.number), "-R", REPO, "--remove-label", f"team:{t}")
    gh("issue", "comment", str(a.number), "-R", REPO, "--body", f"{FREE} {a.reason}".strip())
    set_status(a.number, "Offen")
    print(f"#{a.number} freigegeben")


def cmd_new(a):
    args = ["issue", "create", "-R", REPO, "--title", a.title, "--body-file", a.body_file, "--label", "block"]
    if a.team:
        args += ["--label", f"team:{a.team.lower()}"]
    url = gh(*args).strip()
    number = int(url.rsplit("/", 1)[1])
    set_status(number, "Offen")
    print(url)


def cmd_list(a):
    d = graphql("""query($o:String!,$n:Int!){user(login:$o){projectV2(number:$n){items(first:100){nodes{
        fieldValueByName(name:"Status"){... on ProjectV2ItemFieldSingleSelectValue{name}}
        content{... on Issue{number title state labels(first:10){nodes{name}}}
                ... on PullRequest{number title state labels(first:10){nodes{name}}}}}}}}}""", o=OWNER, n=NUMBER)
    rows = []
    for n in d["user"]["projectV2"]["items"]["nodes"]:
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
    s = sub.add_parser("release"); s.add_argument("number", type=int); s.add_argument("reason", nargs="?", default="")
    a = p.parse_args()
    if a.cmd == "status":
        set_status(a.number, a.column)
        print(f"#{a.number} → {a.column}")
    else:
        {"list": cmd_list, "new": cmd_new, "claim": cmd_claim, "release": cmd_release}[a.cmd](a)


if __name__ == "__main__":
    main()
