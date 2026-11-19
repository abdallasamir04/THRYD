/*
 * Dashboard → Students by class: the tooltip of a point (class, enrolled, capacity, available, over capacity, usage).
 * The details are rendered by the server for each class (.cap-pt__tip); this script shows them in one shared,
 * fixed-position tooltip above the point on hover and on keyboard focus, so the sideways-scrolling plot never clips
 * it. Each class stays a plain link to its students; without JavaScript its aria-label carries the details.
 */
(function () {
    "use strict";

    var doc = document;
    var tip = null, current = null;

    function element() {
        if (!tip) {
            tip = doc.createElement("div");
            tip.className = "cap-tip";
            tip.setAttribute("role", "tooltip");
            tip.id = "cap-tip";
            tip.hidden = true;
            doc.body.appendChild(tip);
        }
        return tip;
    }

    function place(link) {
        var anchor = link.querySelector(".cap-pt__dot"); // the point
        var r = anchor && anchor.getBoundingClientRect().height > 0 ? anchor.getBoundingClientRect() : link.getBoundingClientRect();
        var t = tip.getBoundingClientRect();
        var margin = 8, vw = doc.documentElement.clientWidth, vh = doc.documentElement.clientHeight;
        var left = Math.min(Math.max(margin, r.left + r.width / 2 - t.width / 2), vw - t.width - margin);
        var top = r.top - t.height - 32;                       // above the point and its ratio…
        if (top < margin) top = Math.min(r.top + 12, vh - t.height - margin); // …or over it when there is no room above
        tip.style.left = Math.round(left) + "px";
        tip.style.top = Math.round(top) + "px";
    }

    function show(link) {
        var src = link.parentElement && link.parentElement.querySelector(".cap-pt__tip");
        if (!src) return;
        element().innerHTML = src.innerHTML;
        tip.hidden = false;
        current = link;
        link.setAttribute("aria-describedby", tip.id);
        place(link);
    }

    function hide() {
        if (current) current.removeAttribute("aria-describedby");
        current = null;
        if (tip) tip.hidden = true;
    }

    function columnOf(target) { return target && target.closest ? target.closest("[data-cap-chart] [data-cap-point]") : null; }

    doc.addEventListener("pointerover", function (e) {
        if (e.pointerType === "touch") return; // a tap opens the class; the ratio is by the point
        var link = columnOf(e.target);
        if (link && link !== current) show(link);
    });
    doc.addEventListener("pointerout", function (e) {
        var link = columnOf(e.target);
        if (link && link === current && !link.contains(e.relatedTarget)) hide();
    });
    doc.addEventListener("focusin", function (e) {
        var link = columnOf(e.target);
        if (link) show(link); else if (current) hide();
    });
    doc.addEventListener("focusout", function (e) { if (columnOf(e.target) === current) hide(); });
    doc.addEventListener("keydown", function (e) { if (e.key === "Escape") hide(); });
    // The tooltip is fixed: any scroll (the page or the chart) would leave it behind, so it closes.
    doc.addEventListener("scroll", hide, true);
    window.addEventListener("resize", hide);
})();
