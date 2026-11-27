/*
    PTC live operations: interview timers and the Super Admin live refresh.
    The server is the source of truth: every timer counts from the start time the server recorded (data-timer-start, UTC),
    corrected by the difference between the server's clock and this browser's (data-server-now), so a refresh, coming back
    later or a wrong browser clock never changes what is shown. JavaScript only updates the display.
*/
(function () {
    "use strict";

    // ---------------------------------------------------------------- timers

    var offsets = new WeakMap(); // clock root → (server time − browser time) in ms

    function rootOf(el) { return el.closest("[data-ptc-clock]"); }

    function initClock(root) {
        var server = Date.parse(root.getAttribute("data-server-now") || "");
        offsets.set(root, isNaN(server) ? 0 : server - Date.now());
    }

    function format(ms) {
        var s = Math.max(0, Math.floor(ms / 1000));
        var h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), sec = s % 60;
        return (h > 0 ? h + ":" + String(m).padStart(2, "0") : String(m)) + ":" + String(sec).padStart(2, "0");
    }

    function tick() {
        document.querySelectorAll("[data-timer-start]").forEach(function (el) {
            var start = Date.parse(el.getAttribute("data-timer-start") || "");
            if (isNaN(start)) return;
            var root = rootOf(el);
            var now = Date.now() + (root && offsets.has(root) ? offsets.get(root) : 0);
            var elapsed = now - start;
            el.textContent = format(elapsed);
            var planned = parseFloat(el.getAttribute("data-timer-planned") || "0") * 60000;
            el.classList.toggle("is-over", planned > 0 && elapsed > planned);
        });
    }

    document.querySelectorAll("[data-ptc-clock]").forEach(initClock);
    tick();
    setInterval(tick, 1000);

    // ---------------------------------------------------------------- teacher Start / End (the server answers with its own times)

    document.addEventListener("submit", function (e) {
        var form = e.target;
        if (!form.matches || !form.matches("form[data-ptc-action]")) return;
        e.preventDefault();
        if (form.dataset.busy) return;            // one request per click
        form.dataset.busy = "1";
        form.querySelectorAll("button").forEach(function (b) { b.disabled = true; });
        var token = form.querySelector('input[name="__RequestVerificationToken"]');
        fetch(form.action, {
            method: "POST", body: new FormData(form), credentials: "same-origin",
            headers: { "X-Requested-With": "XMLHttpRequest", "RequestVerificationToken": token ? token.value : "" },
        }).then(function (r) { return r.json().catch(function () { return { success: false, message: "Unexpected answer from the server." }; }); })
          .then(function (data) {
              if (!data.success) alert(data.message || "The interview could not be updated.");
              window.location.reload();           // show the authoritative state
          })
          .catch(function () { alert("Connection problem: nothing was changed. Please try again."); delete form.dataset.busy; form.querySelectorAll("button").forEach(function (b) { b.disabled = false; }); });
    });

    // ---------------------------------------------------------------- Super Admin live refresh

    var box = document.querySelector("[data-live-url]");
    if (!box) return;
    var url = box.getAttribute("data-live-url");
    var base = Math.max(5, parseInt(box.getAttribute("data-refresh-seconds") || "15", 10)) * 1000;
    var delay = base, busy = false, status = document.querySelector("[data-live-status]");

    function paused() {
        // Never refresh away a correction being written, or while the page is not visible.
        return document.hidden || box.querySelector("details[open]") || (document.activeElement && box.contains(document.activeElement) && document.activeElement.matches("input, select, textarea"));
    }

    function schedule() { setTimeout(refresh, delay); }

    function refresh() {
        if (busy || paused()) { schedule(); return; }
        busy = true;
        var controller = "AbortController" in window ? new AbortController() : null;
        var timeout = controller ? setTimeout(function () { controller.abort(); }, 10000) : null;
        fetch(url, { credentials: "same-origin", headers: { "X-Requested-With": "XMLHttpRequest" }, signal: controller ? controller.signal : undefined })
            .then(function (r) { if (!r.ok) throw new Error(r.status); return r.text(); })
            .then(function (html) {
                box.innerHTML = html;
                box.querySelectorAll("[data-ptc-clock]").forEach(initClock);
                tick();
                delay = base;
                if (status) status.textContent = "Updated " + new Date().toLocaleTimeString();
            })
            .catch(function () {
                delay = Math.min(delay * 2, 120000); // back off; keep the last good view
                if (status) status.textContent = "Connection problem: retrying in " + Math.round(delay / 1000) + " s";
            })
            .finally(function () { if (timeout) clearTimeout(timeout); busy = false; schedule(); });
    }

    schedule();
    document.addEventListener("visibilitychange", function () { if (!document.hidden && !busy) { delay = base; } });
})();
