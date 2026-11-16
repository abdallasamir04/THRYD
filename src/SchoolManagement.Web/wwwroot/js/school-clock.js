/*
 * Global school clock (Africa/Cairo). The server renders the current Egyptian time and the exact UTC instant
 * (data-utc). This script keeps ticking from that instant — corrected by the difference between the server's clock
 * and the browser's, so a wrong browser clock does not matter — and formats it in the SCHOOL's time zone with the
 * browser's IANA data (Intl), never the browser's own zone. If that data disagrees with the server's offset (old
 * browser data), the server's offset is used. One timer only, aligned to whole seconds; no requests to the server.
 *
 * window.SchoolClock exposes the same time to other scripts: date() "yyyy-mm-dd" and time() "HH:mm:ss" in school time
 * (arrival "Now" buttons, date shortcuts). Every official calculation is still done on the server.
 */
(function () {
    "use strict";

    var el = document.querySelector("[data-school-clock]");
    if (window.SchoolClock && window.SchoolClock.stop) window.SchoolClock.stop(); // never two competing timers
    if (!el) return;

    var tz = el.getAttribute("data-tz") || "Africa/Cairo";
    var location = el.getAttribute("data-location") || "Cairo, Egypt";
    var serverOffset = parseInt(el.getAttribute("data-offset"), 10); // minutes, at render time
    var skew = parseInt(el.getAttribute("data-utc"), 10) - Date.now(); // server clock − browser clock
    if (!isFinite(skew)) skew = 0;
    var dateEl = el.querySelector("[data-clock-date]");
    var timeEl = el.querySelector("[data-clock-time]");
    var zoneEl = el.querySelector("[data-clock-zone]");
    var hmsEl = el.querySelector("[data-clock-hms]");
    var ampmEl = el.querySelector("[data-clock-ampm]");
    var secEl = el.querySelector("[data-clock-sec]"); // seconds in their own element (hidden on narrow phones by CSS)
    var WEEKDAYS = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
    var MONTHS = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    var fmt = null;
    try {
        fmt = new Intl.DateTimeFormat("en-US", {
            timeZone: tz, hourCycle: "h23", year: "numeric", month: "numeric", day: "numeric", hour: "numeric", minute: "numeric", second: "numeric",
        });
    } catch (e) { fmt = null; } // no time zone support: the server's offset is used

    function pad(n) { return (n < 10 ? "0" : "") + n; }

    /** Wall-clock fields of an instant in school time, plus the offset in minutes. */
    function wall(ms) {
        if (fmt) {
            var p = {};
            fmt.formatToParts(new Date(ms)).forEach(function (x) { p[x.type] = parseInt(x.value, 10); });
            var h = p.hour === 24 ? 0 : p.hour;
            var offset = Math.round((Date.UTC(p.year, p.month - 1, p.day, h, p.minute, p.second) - Math.floor(ms / 1000) * 1000) / 60000);
            // Browser time zone data that disagrees with the server at load (outdated rules): trust the server.
            if (useServer) return fixed(ms);
            return { y: p.year, mo: p.month, d: p.day, h: h, mi: p.minute, s: p.second, offset: offset };
        }
        return fixed(ms);
    }
    function fixed(ms) {
        var d = new Date(ms + serverOffset * 60000);
        return { y: d.getUTCFullYear(), mo: d.getUTCMonth() + 1, d: d.getUTCDate(), h: d.getUTCHours(), mi: d.getUTCMinutes(), s: d.getUTCSeconds(), offset: serverOffset };
    }
    var useServer = false;
    if (fmt && isFinite(serverOffset)) useServer = wall(Date.now() + skew).offset !== serverOffset;
    if (!isFinite(serverOffset)) serverOffset = 0;

    function now() { return Date.now() + skew; }

    function render() {
        var w = wall(now());
        var weekday = WEEKDAYS[new Date(Date.UTC(w.y, w.mo - 1, w.d)).getUTCDay()];
        var h12 = w.h % 12 === 0 ? 12 : w.h % 12;
        var sign = w.offset < 0 ? "-" : "+";
        var abs = Math.abs(w.offset);
        var label = "UTC" + sign + pad(Math.floor(abs / 60)) + ":" + pad(abs % 60);
        var ampm = w.h < 12 ? "AM" : "PM";
        if (dateEl) {
            // Compact "Sun, 15 Nov 2026"; the full date is the tooltip of its container.
            dateEl.textContent = weekday.slice(0, 3) + ", " + w.d + " " + MONTHS[w.mo - 1].slice(0, 3) + " " + w.y;
            dateEl.setAttribute("datetime", w.y + "-" + pad(w.mo) + "-" + pad(w.d));
            if (dateEl.parentNode && dateEl.parentNode.setAttribute) dateEl.parentNode.setAttribute("title", weekday + ", " + w.d + " " + MONTHS[w.mo - 1] + " " + w.y);
        }
        if (timeEl) {
            var hm = pad(h12) + ":" + pad(w.mi);
            if (hmsEl && ampmEl && secEl) { hmsEl.textContent = hm; secEl.textContent = ":" + pad(w.s); ampmEl.textContent = ampm; }
            else timeEl.textContent = hm + ":" + pad(w.s) + " " + ampm;
            timeEl.setAttribute("datetime", w.y + "-" + pad(w.mo) + "-" + pad(w.d) + "T" + pad(w.h) + ":" + pad(w.mi) + ":" + pad(w.s) + label.slice(3));
        }
        if (zoneEl) zoneEl.textContent = location + " · " + label;
        // Other displays of the same school time (e.g. the dashboard hero): "15 Nov 2026 · 08:16 PM", same tick, no second timer.
        var others = document.querySelectorAll("[data-school-now]");
        for (var i = 0; i < others.length; i++)
            others[i].textContent = w.d + " " + MONTHS[w.mo - 1].slice(0, 3) + " " + w.y + " · " + pad(h12) + ":" + pad(w.mi) + " " + ampm;
        return w;
    }

    var timer = null;
    function tick() {
        render();
        timer = setTimeout(tick, 1000 - (now() % 1000) + 15); // next whole second, no drift
    }
    function stop() { if (timer) clearTimeout(timer); timer = null; }

    // A tab coming back from the background catches up at once.
    document.addEventListener("visibilitychange", function () { if (!document.hidden) { stop(); tick(); } });

    window.SchoolClock = {
        zone: tz,
        stop: stop,
        now: now,
        /** School date "yyyy-mm-dd", optionally shifted by whole days. */
        date: function (addDays) {
            var w = wall(now());
            var d = new Date(Date.UTC(w.y, w.mo - 1, w.d + (addDays || 0)));
            return d.getUTCFullYear() + "-" + pad(d.getUTCMonth() + 1) + "-" + pad(d.getUTCDate());
        },
        /** School time "HH:mm:ss" (24-hour). */
        time: function () { var w = wall(now()); return pad(w.h) + ":" + pad(w.mi) + ":" + pad(w.s); },
    };
    tick();
})();
