# /// script
# requires-python = ">=3.12"
# dependencies = ["playwright>=1.50", "requests>=2.32"]
# ///
"""Drives the plugin's settings page in Jellyfin's web UI (headless Firefox), as an administrator,
and the player on a share's public page.

usage: uv run ui.py <creds.env> <screenshot-dir>   (run.sh --ui runs it after the API checks)
"""
import hashlib, pathlib, secrets, sys
from urllib.parse import urlsplit

import requests
from playwright.sync_api import sync_playwright

creds = dict(l.split("=", 1) for l in pathlib.Path(sys.argv[1]).read_text().split())
URL, OUT = creds["URL"], pathlib.Path(sys.argv[2])
HEADERS = {"Authorization": f'MediaBrowser Token="{creds["JF_TOKEN"]}"'}
USER = "uitest"
results = []


def check(name, ok, detail=""):
    results.append(ok)
    print(("PASS " if ok else "FAIL ") + name + ("" if ok else f"\n        {str(detail)[:300]}"))


def token_ping(password):
    s = secrets.token_hex(6)
    return requests.get(f"{URL}/opensubsonic/rest/ping", timeout=30, params={
        "u": USER, "t": hashlib.md5((password + s).encode()).hexdigest(), "s": s, "v": "1.16.1", "c": "ui", "f": "json",
    }).json()["subsonic-response"]


def code(resp):
    return resp.get("error", {}).get("code")


def api(endpoint, *params):
    """A Subsonic API call as the admin (password login)."""
    return requests.get(f"{URL}/opensubsonic/rest/{endpoint}", timeout=30, params=[
        ("u", creds["AU"]), ("p", creds["AP"]), ("v", "1.16.1"), ("c", "ui"), ("f", "json"), *params,
    ]).json()["subsonic-response"]


if not any(u["Name"] == USER for u in requests.get(f"{URL}/Users", headers=HEADERS, timeout=30).json()):
    requests.post(f"{URL}/Users/New", headers=HEADERS, json={"Name": USER, "Password": "upass"}, timeout=30).raise_for_status()
users = sorted(u["Name"] for u in requests.get(f"{URL}/Users", headers=HEADERS, timeout=30).json())

with sync_playwright() as p:
    browser = p.firefox.launch()
    page = browser.new_page(viewport={"width": 1280, "height": 900})
    errors = []
    page.goto(f"{URL}/web/#/login")
    page.wait_for_selector("#txtManualName", timeout=60000)
    page.fill("#txtManualName", creds["AU"])
    page.fill("#txtManualPassword", creds["AP"])
    page.click(".manualLoginForm button[type=submit]")
    page.wait_for_url("**/home**", timeout=60000)

    page.goto(f"{URL}/web/#/configurationpage?name=Subfin")
    page.wait_for_selector("#SubfinConfigPage .subfinUser", timeout=60000)
    # Script errors count from here on. Jellyfin 12.1's own screens sometimes throw while the route
    # changes (scrollHandler on the home screen, CancelledError from the dashboard's queries); a
    # failure loading this page shows as missing rows or its error dialog instead.
    page.on("pageerror", lambda e: errors.append(str(e)))
    page.screenshot(path=OUT / "ui-1-page.png")
    server = page.inner_text("#subfinServerPath")
    check("shows the path apps connect to", server == urlsplit(URL).path + "/opensubsonic", server)
    host = urlsplit(URL).netloc
    check("never shows the server's address", host not in page.inner_text("#SubfinConfigPage"), host)
    rows = page.locator("#SubfinConfigPage .subfinUser")
    names = sorted(rows.nth(i).locator(".subfinUserInfo > div").first.inner_text() for i in range(rows.count()))
    check("lists every Jellyfin user", names == users, names)
    row = lambda: page.locator("#SubfinConfigPage .subfinUser").filter(has_text=USER)
    check("a new user has no OpenSubsonic password", "No OpenSubsonic password" in row().inner_text(), row().inner_text())

    row().get_by_role("button", name="Generate").click()
    page.wait_for_selector("#SubfinConfigPage .subfinSecret code", timeout=30000)
    page.screenshot(path=OUT / "ui-2-generated.png")
    shown = page.locator("#SubfinConfigPage .subfinSecret code").all_inner_texts()
    check("Generate shows the path, username and password", len(shown) == 3 and shown[:2] == [server, USER], shown)
    check("... and not the server's address", host not in page.inner_text("#SubfinConfigPage"), host)
    first = shown[-1]
    check("the shown password signs in by token", token_ping(first).get("status") == "ok", token_ping(first))
    check("the row offers Regenerate and Remove",
          row().get_by_role("button", name="Regenerate").count() == 1 and row().get_by_role("button", name="Remove").count() == 1, row().inner_text())

    page.once("dialog", lambda d: d.accept())
    row().get_by_role("button", name="Regenerate").click()
    page.wait_for_function("(old) => { const c = document.querySelectorAll('#SubfinConfigPage .subfinSecret code');"
                           " return c.length === 3 && c[2].textContent !== old; }", arg=first, timeout=30000)
    second = page.locator("#SubfinConfigPage .subfinSecret code").nth(2).inner_text()
    check("Regenerate replaces the password", code(token_ping(first)) == 40 and token_ping(second).get("status") == "ok", second)

    page.once("dialog", lambda d: d.dismiss())
    row().get_by_role("button", name="Remove").click()
    page.wait_for_timeout(1000)
    check("cancelling Remove keeps the password", token_ping(second).get("status") == "ok")
    page.once("dialog", lambda d: d.accept())
    row().get_by_role("button", name="Remove").click()
    page.wait_for_function("(u) => [...document.querySelectorAll('#SubfinConfigPage .subfinUser')]"
                           ".some(r => r.textContent.includes(u) && r.textContent.includes('No OpenSubsonic password'))", arg=USER, timeout=30000)
    check("Remove ends token logins", code(token_ping(second)) == 41)
    check("the password isn't shown again", page.locator("#SubfinConfigPage .subfinSecret").count() == 0)

    was = page.is_checked("#logRestRequests")
    page.click("#SubfinConfigForm .emby-checkbox-label:has(#logRestRequests)")
    page.click("#SubfinConfigForm button[type=submit]")
    page.wait_for_selector("text=Settings saved", timeout=30000)
    cfg = requests.get(f"{URL}/Plugins/4a3b2c1d-e5f6-7890-abcd-ef1234567890/Configuration", headers=HEADERS, timeout=30).json()
    check("the settings form saves", cfg.get("LogRestRequests") is (not was), cfg)
    check("no script errors on the page", not errors, errors)

    # A share's public page plays the shared songs in the browser
    album = next(a for a in api("getAlbumList2", ("type", "alphabeticalByName"))["albumList2"]["album"] if a["name"] == "Double Album")
    share = api("createShare", ("id", album["id"]))["shares"]["share"][0]
    ids = [e["id"] for e in share["entry"]]
    player = browser.new_page()
    share_errors = []
    player.on("pageerror", lambda e: share_errors.append(str(e)))
    player.goto(share["url"])
    player.wait_for_selector("#tracklist li", timeout=30000)
    titles = player.locator("#tracklist .track-title").all_inner_texts()
    check("the share page lists the shared album's songs", titles == ["Disc 1 Track 1", "Disc 1 Track 2", "Disc 2 Track 1", "Disc 2 Track 2"], titles)
    check("... and links its M3U and ZIP", player.locator("a.download-link").count() == 2, player.locator("a.download-link").all_inner_texts())
    # The player loads nothing before a click (preload="none", and browsers block autoplay)
    loaded = "(id) => { const a = document.getElementById('audio'); return a.src.includes('id=' + id) && a.duration > 0 && a.error === null; }"
    for i, name in ((0, "clicking the first song plays it"), (2, "clicking another song plays it")):
        player.click(f"#tr-{i}")
        try:
            player.wait_for_function(loaded, arg=ids[i], timeout=30000)
            check(name, player.locator(f"#tr-{i}.active").count() == 1)
        except Exception:
            check(name, False, player.evaluate("[document.getElementById('audio').src, String(document.getElementById('audio').error?.message)]"))
    player.screenshot(path=OUT / "ui-3-share.png")
    check("no script errors on the share page", not share_errors, share_errors)
    api("deleteShare", ("id", share["id"]))
    browser.close()

print(f"{len(results)} UI checks, {results.count(True)} passed, {results.count(False)} failed")
sys.exit(0 if all(results) else 1)
