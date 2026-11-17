/*
 * THRYD selection controls: one component system for every dropdown, date, time and date-time field.
 * Styles and theme tokens: css/thryd-ui.css. Every control is set up automatically, on page load and whenever new
 * HTML arrives (AJAX lists, modals, templates), so pages never opt in.
 *
 *  Dropdown          <select>                  The select stays the visible, focusable field: its layout, label,
 *                                              validation classes and the scripts that rebuild options, set .value or
 *                                              toggle disabled keep working unchanged. Only the popup is replaced.
 *  Searchable        <select data-searchable>  (or any select with more than 10 options) adds a search box.
 *                    data-search-notes         the options' data-note is searched too.
 *                    data-no-search            never show the search box.
 *  Multi-select      <select multiple>         checklist with Select all / Clear.
 *  Date picker       <input type="date">       calendar with month and year views, Today, Clear; min / max honoured.
 *  Time picker       <input type="time">       hour / minute / AM-PM columns; min / max / step honoured.
 *  Date-time picker  <input type="datetime-local">
 *
 * For date, time and multi-select fields the native input stays in the form with the same id, name, type and value,
 * so model binding, required / min / max validation and every change listener are untouched. It is visually hidden
 * and an input-styled button (with the input's own classes) opens the picker. Values are never reformatted: dates
 * post yyyy-mm-dd, times HH:mm (24-hour), date-times yyyy-mm-ddTHH:mm. "Today" and "Now" are the school's
 * date and time (SchoolClock, Africa/Cairo), not the browser's.
 *
 *  data-native                      keep the browser's own control for this field.
 *  data-empty-text="…"              text shown while the field is empty.
 *  data-quick-times="08:00,12:00"   shortcut chips in the time picker.
 *  data-clears="#Other"             clearing this field also clears that one.
 *
 * Popups live in the top layer (Popover API) with fixed positioning: no overflow, stacking context or open dialog
 * can clip or cover them. They open below the field, flip above when there is no room, stay inside the viewport,
 * follow the field while the page scrolls, and close on Escape, a click outside or Tab.
 *
 * Public API: ThrydUI.enhance(root), ThrydUI.close(), ThrydUI.sync(field).
 */
(function () {
    "use strict";

    var doc = document;
    var MONTHS = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];
    var DAYS = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
    var SEARCH_THRESHOLD = 10;
    var canPopover = typeof HTMLElement !== "undefined" && Object.prototype.hasOwnProperty.call(HTMLElement.prototype, "popover");
    var reducedMotion = window.matchMedia ? window.matchMedia("(prefers-reduced-motion: reduce)") : { matches: false };
    var seq = 0;

    // ------------------------------------------------------------------ helpers
    function $$(sel, root) { return Array.prototype.slice.call((root || doc).querySelectorAll(sel)); }
    function pad(n) { return (n < 10 ? "0" : "") + n; }
    function uid(kind) { return "th-" + kind + "-" + (++seq); }
    function esc(s) {
        return String(s == null ? "" : s).replace(/[&<>"']/g, function (c) {
            return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c];
        });
    }
    function icon(name, cls) { return '<svg class="icon ' + (cls || "") + '" aria-hidden="true"><use href="#i-' + name + '"></use></svg>'; }
    function fire(el, type) { el.dispatchEvent(new Event(type, { bubbles: true })); }
    function focus(el) { if (!el) return; try { el.focus({ preventScroll: true }); } catch (e) { el.focus(); } }
    function rtl(el) { return getComputedStyle(el).direction === "rtl"; }
    function cssEscape(s) { return window.CSS && CSS.escape ? CSS.escape(s) : String(s).replace(/["\\]/g, "\\$&"); }

    /** Keeps a scrolling box's own scroll position so an item is visible (never scrolls the page). */
    function keepVisible(box, el, center) {
        var top = el.offsetTop, bottom = top + el.offsetHeight;
        if (center) { box.scrollTop = top - (box.clientHeight - el.offsetHeight) / 2; return; }
        if (top < box.scrollTop) box.scrollTop = top - 4;
        else if (bottom > box.scrollTop + box.clientHeight) box.scrollTop = bottom - box.clientHeight + 4;
    }

    function labelFor(field) {
        var label = field.id ? doc.querySelector('label[for="' + cssEscape(field.id) + '"]') : null;
        return label || field.closest("label");
    }
    function nameOf(field) {
        var label = labelFor(field);
        var text = label ? label.textContent.replace(/\*/g, "").replace(/\s+/g, " ").trim() : "";
        return text || field.getAttribute("aria-label") || field.getAttribute("title") || "options";
    }

    // Dates are plain calendar days ("yyyy-mm-dd") handled in UTC, so no browser time zone or DST can shift them.
    function parseIso(s) {
        var m = /^(\d{4})-(\d{2})-(\d{2})/.exec(s || "");
        return m ? new Date(Date.UTC(+m[1], +m[2] - 1, +m[3])) : null;
    }
    function iso(d) { return d.getUTCFullYear() + "-" + pad(d.getUTCMonth() + 1) + "-" + pad(d.getUTCDate()); }
    function addDays(d, n) { return new Date(Date.UTC(d.getUTCFullYear(), d.getUTCMonth(), d.getUTCDate() + n)); }
    function monthStart(y, m) { return new Date(Date.UTC(y, m, 1)); }
    function todayIso() {
        if (window.SchoolClock && window.SchoolClock.date) return window.SchoolClock.date(0);
        var n = new Date();
        return n.getFullYear() + "-" + pad(n.getMonth() + 1) + "-" + pad(n.getDate());
    }
    function longDate(d) { return DAYS[d.getUTCDay()].slice(0, 3) + ", " + d.getUTCDate() + " " + MONTHS[d.getUTCMonth()].slice(0, 3) + " " + d.getUTCFullYear(); }

    // Times are { h: 0–23, m: 0–59 }; the field always holds 24-hour "HH:mm".
    function parseHm(s) {
        var m = /(?:^|T)(\d{1,2}):(\d{2})/.exec(s || "");
        return m && +m[1] < 24 && +m[2] < 60 ? { h: +m[1], m: +m[2] } : null;
    }
    function hm(t) { return pad(t.h) + ":" + pad(t.m); }
    function label12(t) { return (t.h % 12 || 12) + ":" + pad(t.m) + " " + (t.h >= 12 ? "PM" : "AM"); }
    function nowHm() {
        if (window.SchoolClock && window.SchoolClock.time) return window.SchoolClock.time().slice(0, 5);
        var n = new Date();
        return pad(n.getHours()) + ":" + pad(n.getMinutes());
    }

    /** Scripts set field.value directly (filters, quick buttons, linked fields): the visible field follows at once. */
    function hookValue(el, after) {
        var proto = Object.getPrototypeOf(el);
        ["value", "selectedIndex"].forEach(function (prop) {
            var p = proto, d = null;
            while (p && !d) { d = Object.getOwnPropertyDescriptor(p, prop); p = Object.getPrototypeOf(p); }
            if (!d || !d.set) return;
            Object.defineProperty(el, prop, {
                configurable: true, enumerable: true,
                get: function () { return d.get.call(this); },
                set: function (v) { d.set.call(this, v); after(); }
            });
        });
    }

    function revalidate(el) {
        if (!window.jQuery || !el.form) return;
        var $form = window.jQuery(el.form);
        if (!$form.data("validator")) return;
        window.jQuery(el).valid();
        var other = el.getAttribute("data-revalidate");
        var field = other && el.form.querySelector(other);
        if (field && field.value) { window.jQuery(field).removeData("previousValue"); window.jQuery(field).valid(); }
    }

    // ------------------------------------------------------------------ popup layer (one open popup at a time)
    var Layer = (function () {
        var cur = null;
        var frame = 0;

        function host(anchor) {
            // Inside a modal dialog the popup must be part of the dialog, or the dialog makes it inert.
            return anchor.closest("dialog[open]") || doc.body;
        }

        function open(anchor, panel, options) {
            close(false, true);
            options = options || {};
            panel.classList.add("th-pop");
            if (canPopover) panel.setAttribute("popover", "manual");
            host(anchor).appendChild(panel);
            if (canPopover) { try { panel.showPopover(); } catch (e) { /* already shown or unsupported here */ } }
            cur = { anchor: anchor, panel: panel, options: options };
            place();
            requestAnimationFrame(function () { if (cur && cur.panel === panel) panel.classList.add("is-open"); });
            panel.addEventListener("keydown", onPanelKey);
            return cur;
        }

        function close(returnFocus, immediate) {
            if (!cur) return;
            var c = cur;
            cur = null;
            var p = c.panel;
            var remove = function () {
                if (canPopover) { try { p.hidePopover(); } catch (e) { /* already hidden */ } }
                p.remove();
            };
            if (immediate || reducedMotion.matches) remove();
            else { p.classList.remove("is-open"); p.classList.add("is-closing"); setTimeout(remove, 140); }
            if (c.options.onClose) c.options.onClose();
            if (returnFocus && c.anchor.isConnected) focus(c.anchor);
        }

        function place() {
            if (!cur) return;
            var a = cur.anchor, p = cur.panel, o = cur.options;
            if (!a.isConnected) { close(false, true); return; }
            var r = a.getBoundingClientRect();
            var vw = doc.documentElement.clientWidth, vh = window.innerHeight;
            // The field was scrolled out of view or hidden: the popup has nothing to point at any more.
            if ((r.width === 0 && r.height === 0) || r.bottom < 0 || r.top > vh) { close(false, true); return; }
            var gap = 6, edge = 8;
            p.style.maxHeight = "";
            p.style.minWidth = o.matchWidth ? Math.min(vw - edge * 2, Math.max(r.width, o.minWidth || 0)) + "px" : "";
            p.style.left = "0px";
            p.style.top = "0px";
            var pw = p.offsetWidth, ph = p.offsetHeight;
            var below = vh - r.bottom - gap - edge, above = r.top - gap - edge;
            var up = ph > below && above > below;
            var room = Math.max(140, up ? above : below);
            if (ph > room) { p.style.maxHeight = room + "px"; ph = room; }
            var top = up ? r.top - gap - ph : r.bottom + gap;
            var left = rtl(a) ? r.right - pw : r.left;
            left = Math.min(Math.max(edge, left), Math.max(edge, vw - pw - edge));
            p.style.left = Math.round(left) + "px";
            p.style.top = Math.round(Math.max(edge, top)) + "px";
            p.setAttribute("data-side", up ? "top" : "bottom");
        }
        function schedule() {
            if (!cur || frame) return;
            frame = requestAnimationFrame(function () { frame = 0; place(); });
        }

        function onPanelKey(e) {
            if (!cur) return;
            if (e.key === "Escape") {
                // Handled here, so an open modal dialog does not close as well.
                e.preventDefault();
                e.stopPropagation();
                close(true);
            } else if (e.key === "Tab") {
                if (cur.options.trapTab) {
                    // Calendar / clock popups keep Tab inside until they are closed.
                    var items = $$("button:not([disabled]), input:not([disabled]), [tabindex='0']", cur.panel).filter(function (el) { return el.offsetParent !== null; });
                    if (!items.length) return;
                    var first = items[0], last = items[items.length - 1];
                    if (e.shiftKey && doc.activeElement === first) { e.preventDefault(); focus(last); }
                    else if (!e.shiftKey && doc.activeElement === last) { e.preventDefault(); focus(first); }
                } else {
                    // Lists: focus goes back to the field and the browser moves on from there, as with a native select.
                    close(true, true);
                }
            }
        }

        doc.addEventListener("pointerdown", function (e) {
            if (!cur || cur.panel.contains(e.target) || cur.anchor.contains(e.target)) return;
            close(false);
        }, true);
        doc.addEventListener("focusin", function (e) {
            if (!cur || cur.panel.contains(e.target) || cur.anchor.contains(e.target)) return;
            close(false);
        });
        doc.addEventListener("scroll", function (e) {
            if (cur && !(e.target.nodeType === 1 && cur.panel.contains(e.target))) schedule();
        }, true);
        window.addEventListener("resize", schedule);
        if (window.visualViewport) window.visualViewport.addEventListener("resize", schedule);
        // A dialog closing (or its content being replaced) takes its popup with it.
        doc.addEventListener("close", function (e) {
            if (cur && e.target.contains && e.target.contains(cur.anchor)) close(false, true);
        }, true);

        return {
            open: open,
            close: close,
            place: place,
            schedule: schedule,
            isOpenFor: function (anchor) { return !!cur && cur.anchor === anchor; },
            panel: function () { return cur ? cur.panel : null; }
        };
    })();

    // ------------------------------------------------------------------ option list (dropdown, searchable, multi)
    function readOptions(select) {
        var required = select.required || select.hasAttribute("data-val-required");
        var out = [];
        Array.prototype.forEach.call(select.options, function (o, i) {
            if (o.hidden) return;
            var group = o.parentNode && o.parentNode.nodeName === "OPTGROUP" ? o.parentNode : null;
            var placeholder = o.value === "" && !select.multiple;
            // "Choose…" is not a choice when the field is required (or the option itself is disabled).
            if (placeholder && (required || o.disabled)) return;
            out.push({
                index: i, option: o, text: o.text, note: o.getAttribute("data-note") || "",
                group: group ? group.label : null, disabled: o.disabled || !!(group && group.disabled), placeholder: placeholder
            });
        });
        return out;
    }

    function highlight(text, term) {
        if (!term) return esc(text);
        var at = text.toLowerCase().indexOf(term);
        if (at < 0) return esc(text);
        return esc(text.slice(0, at)) + "<mark>" + esc(text.slice(at, at + term.length)) + "</mark>" + esc(text.slice(at + term.length));
    }

    /**
     * The popup body shared by the dropdown and the multi-select: optional search, grouped options, keyboard
     * navigation (arrows, Home / End, Page Up / Down, type-ahead), empty state.
     */
    function OptionList(select, cfg) {
        var listId = uid("list");
        var multi = !!cfg.multi;
        var name = nameOf(select);
        var panel = doc.createElement("div");
        panel.className = "th-pop--list" + (multi ? " th-pop--multi" : "");
        var html = "";
        if (cfg.search) {
            html += '<div class="th-search">' + icon("search") +
                '<input class="th-search__input" type="text" autocomplete="off" spellcheck="false" placeholder="Search…" role="combobox"' +
                ' aria-autocomplete="list" aria-expanded="true" aria-controls="' + listId + '" aria-label="Search ' + esc(name) + '" /></div>';
        }
        if (multi) {
            html += '<div class="th-multi-bar"><span class="th-multi-bar__count" aria-live="polite"></span>' +
                '<button type="button" class="th-link" data-th-all>Select all</button><button type="button" class="th-link" data-th-none>Clear</button></div>';
        }
        html += '<ul class="th-list" id="' + listId + '" role="listbox" tabindex="-1" aria-label="' + esc(name) + '"' + (multi ? ' aria-multiselectable="true"' : "") + "></ul>" +
            '<p class="th-empty" hidden>' + icon("search") + "<span></span></p>";
        if (multi) html += '<div class="th-foot"><span></span><button type="button" class="btn btn--primary btn--sm" data-th-done>Done</button></div>';
        panel.innerHTML = html;

        var list = panel.querySelector(".th-list");
        var search = panel.querySelector(".th-search__input");
        var empty = panel.querySelector(".th-empty");
        var keyTarget = search || list;
        var items = [], active = -1, typed = "", typedAt = 0, term = "";

        function isSelected(o) { return multi ? o.option.selected : select.selectedIndex === o.index; }

        function render() {
            var t = term.trim().toLowerCase();
            var notes = select.hasAttribute("data-search-notes");
            items = readOptions(select).filter(function (o) {
                return !t || (o.text + (notes ? " " + o.note : "")).toLowerCase().indexOf(t) >= 0;
            });
            var out = "", group = null;
            items.forEach(function (o, i) {
                if (o.group !== group) {
                    group = o.group;
                    if (group) out += '<li class="th-group" role="presentation">' + esc(group) + "</li>";
                }
                var sel = isSelected(o);
                out += '<li class="th-option" role="option" id="' + listId + "-" + i + '" data-i="' + i + '" aria-selected="' + sel + '"' + (o.disabled ? ' aria-disabled="true"' : "") + ">" +
                    (multi ? '<span class="th-check" aria-hidden="true">' + icon("check") + "</span>" : "") +
                    '<span class="th-option__text"><span class="th-option__label">' + highlight(o.text, t) + "</span>" +
                    (o.note ? '<span class="th-option__note">' + highlight(o.note, notes ? t : "") + "</span>" : "") + "</span>" +
                    (multi ? "" : '<span class="th-option__tick" aria-hidden="true">' + icon("check") + "</span>") + "</li>";
            });
            list.innerHTML = out;
            empty.hidden = items.length > 0;
            if (!items.length) empty.querySelector("span").textContent = t ? "No matches for “" + term.trim() + "”" : "No options available";
            if (multi) counter();
            Layer.schedule();
        }

        function counter() {
            var all = readOptions(select).filter(function (o) { return !o.disabled; });
            var n = all.filter(function (o) { return o.option.selected; }).length;
            panel.querySelector(".th-multi-bar__count").textContent = n + " of " + all.length + " selected";
        }

        function setActive(i, scroll) {
            active = i;
            $$(".th-option.is-active", list).forEach(function (li) { li.classList.remove("is-active"); });
            var li = i >= 0 ? list.querySelector('[data-i="' + i + '"]') : null;
            if (li) {
                li.classList.add("is-active");
                keyTarget.setAttribute("aria-activedescendant", li.id);
                if (scroll !== false) keepVisible(list, li, scroll === "center");
            } else keyTarget.removeAttribute("aria-activedescendant");
        }
        function step(from, dir, count) {
            var i = from, found = from;
            for (var moved = 0; moved < (count || 1);) {
                i += dir;
                if (i < 0 || i >= items.length) break;
                if (!items[i].disabled) { found = i; moved++; }
            }
            return found;
        }
        function firstEnabled() { return step(-1, 1); }
        function lastEnabled() { return step(items.length, -1); }
        function initialActive() {
            for (var i = 0; i < items.length; i++) if (isSelected(items[i]) && !items[i].disabled) return i;
            return firstEnabled();
        }

        function typeahead(ch) {
            var now = Date.now();
            typed = now - typedAt > 700 ? ch : typed + ch;
            typedAt = now;
            var t = typed.toLowerCase();
            var start = typed.length === 1 ? active + 1 : Math.max(active, 0);
            for (var n = 0; n < items.length; n++) {
                var i = (start + n) % items.length;
                if (!items[i].disabled && items[i].text.toLowerCase().indexOf(t) === 0) { setActive(i, "center"); return; }
            }
        }

        function choose(i) {
            var o = items[i];
            if (!o || o.disabled) return;
            if (!multi) { cfg.onPick(o); return; }
            o.option.selected = !o.option.selected;
            var li = list.querySelector('[data-i="' + i + '"]');
            if (li) li.setAttribute("aria-selected", String(o.option.selected));
            counter();
            cfg.onToggle();
        }

        function onKey(e) {
            var k = e.key;
            var inSearch = e.target === search;
            if (k === "ArrowDown") { e.preventDefault(); setActive(active < 0 ? firstEnabled() : step(active, 1)); }
            else if (k === "ArrowUp") { e.preventDefault(); setActive(active < 0 ? lastEnabled() : step(active, -1)); }
            else if (k === "PageDown") { e.preventDefault(); setActive(step(Math.max(active, -1), 1, 8)); }
            else if (k === "PageUp") { e.preventDefault(); setActive(step(active < 0 ? items.length : active, -1, 8)); }
            else if ((k === "Home" || k === "End") && (!inSearch || e.ctrlKey)) { e.preventDefault(); setActive(k === "Home" ? firstEnabled() : lastEnabled()); }
            else if (k === "Enter") { e.preventDefault(); if (active >= 0) choose(active); else if (multi) cfg.onDone(); }
            else if (k === " " && !inSearch) { e.preventDefault(); if (active >= 0) choose(active); }
            else if (!inSearch && k.length === 1 && !e.ctrlKey && !e.metaKey && !e.altKey) { e.preventDefault(); typeahead(k); }
        }

        keyTarget.addEventListener("keydown", onKey);
        if (search) {
            search.addEventListener("input", function () {
                term = search.value;
                render();
                setActive(firstEnabled(), "start");
                list.scrollTop = 0;
            });
        }
        // Clicks never take focus away from the search box / list, so keyboard and mouse can be mixed.
        list.addEventListener("mousedown", function (e) { e.preventDefault(); });
        list.addEventListener("click", function (e) {
            var li = e.target.closest(".th-option");
            if (li) choose(+li.getAttribute("data-i"));
        });
        list.addEventListener("mousemove", function (e) {
            var li = e.target.closest(".th-option");
            if (li && !li.classList.contains("is-active") && li.getAttribute("aria-disabled") !== "true") setActive(+li.getAttribute("data-i"), false);
        });
        panel.addEventListener("click", function (e) {
            var b = e.target.closest("button");
            if (!b) return;
            if (b.hasAttribute("data-th-done")) cfg.onDone();
            else if (b.hasAttribute("data-th-all") || b.hasAttribute("data-th-none")) {
                var on = b.hasAttribute("data-th-all");
                items.forEach(function (o) { if (!o.disabled) o.option.selected = on; });
                render();
                setActive(active, false);
                cfg.onToggle();
            }
        });

        render();

        return {
            panel: panel,
            listId: listId,
            start: function (how) {
                how = how || {};
                setActive(initialActive(), "center");
                // On touch screens the list gets focus, not the search box, so the keyboard does not cover the list.
                focus(how.touch || !search ? list : search);
                if (how.typed) {
                    if (search) { search.value = how.typed; term = how.typed; render(); setActive(firstEnabled(), "start"); }
                    else typeahead(how.typed);
                } else if (how.key === "End") setActive(lastEnabled());
                else if (how.key === "Home") setActive(firstEnabled());
            }
        };
    }

    function wantsSearch(select) {
        if (select.hasAttribute("data-no-search")) return false;
        return select.hasAttribute("data-searchable") || select.options.length > SEARCH_THRESHOLD;
    }

    // ------------------------------------------------------------------ dropdown (single select)
    function initSelect(select) {
        select._thryd = true;
        select.classList.add("th-select");
        select.setAttribute("aria-haspopup", "listbox");
        select.setAttribute("aria-expanded", "false");
        var touch = null;

        function open(how) {
            if (select.disabled || Layer.isOpenFor(select)) return;
            var ui = OptionList(select, { search: wantsSearch(select), onPick: pick });
            Layer.open(select, ui.panel, {
                matchWidth: true, minWidth: 200,
                onClose: function () { select.setAttribute("aria-expanded", "false"); select.removeAttribute("aria-controls"); }
            });
            select.setAttribute("aria-expanded", "true");
            select.setAttribute("aria-controls", ui.listId);
            ui.start(how);
            Layer.place();
        }
        function toggle(how) { if (Layer.isOpenFor(select)) Layer.close(true); else open(how); }
        function pick(o) {
            var changed = select.selectedIndex !== o.index;
            select.selectedIndex = o.index;
            Layer.close(true);
            if (changed) { fire(select, "input"); fire(select, "change"); }
            revalidate(select);
        }

        // Mouse: no native menu, our popup instead (the select still takes focus).
        select.addEventListener("mousedown", function (e) {
            if (e.button !== 0) return;
            e.preventDefault();
            if (select.disabled) return;
            focus(select);
            toggle({});
        });
        // Touch: phones open their own picker sheet on tap; a cancelled touchend stops it (scroll gestures are left alone).
        select.addEventListener("touchstart", function (e) {
            var t = e.touches[0];
            touch = t ? { x: t.clientX, y: t.clientY } : null;
        }, { passive: true });
        select.addEventListener("touchend", function (e) {
            var t = e.changedTouches[0], start = touch;
            touch = null;
            if (!start || !t || !e.cancelable || Math.abs(t.clientX - start.x) > 10 || Math.abs(t.clientY - start.y) > 10) return;
            e.preventDefault();
            if (select.disabled) return;
            focus(select);
            toggle({ touch: true });
        });
        // Keyboard: the keys that would open (or silently change) the native select open the THRYD list.
        select.addEventListener("keydown", function (e) {
            if (e.ctrlKey || e.metaKey) return;
            var k = e.key;
            if (["ArrowDown", "ArrowUp", "Enter", " ", "F4", "Home", "End", "PageUp", "PageDown"].indexOf(k) >= 0) {
                e.preventDefault();
                open({ key: k });
            } else if (k.length === 1 && !e.altKey) {
                e.preventDefault();
                open({ typed: k });
            }
        });
    }

    // ------------------------------------------------------------------ trigger button (date, time, date-time, multi)
    var CARRY_SKIP = { "picker": 1, "th-native": 1, "input-validation-error": 1, "valid": 1, "is-invalid": 1 };

    function makeTrigger(field, iconName, kind, popup) {
        var id = field.id || uid(kind);
        var button = doc.createElement("button");
        button.type = "button";
        // The field's own classes (sizes, layout hooks such as week-nav__picker) carry over to the visible button.
        var classes = (field.className || "").split(/\s+/).filter(function (c) { return c && !CARRY_SKIP[c]; })
            .map(function (c) { return c === "select" ? "input" : c; });
        if (classes.indexOf("input") < 0) classes.unshift("input");
        button.className = classes.join(" ") + " th-trigger th-trigger--" + kind;
        button.id = id + "-trigger";
        button.setAttribute("aria-haspopup", popup);
        button.setAttribute("aria-expanded", "false");
        button.innerHTML = (iconName ? icon(iconName, "th-trigger__icon") : "") +
            '<span class="th-trigger__value" id="' + id + '-value"></span>' + icon("chevron-down", "th-trigger__chevron");
        field.parentNode.insertBefore(button, field);

        field.classList.add("th-native");
        field.tabIndex = -1;
        field.setAttribute("aria-hidden", "true");
        field._thrydTrigger = button;

        var label = labelFor(field);
        if (label && label.contains(field)) label = null; // a wrapping label would also wrap the button
        if (label) {
            label.id = label.id || id + "-label";
            button.setAttribute("aria-labelledby", label.id + " " + id + "-value");
        } else if (field.getAttribute("aria-label")) {
            button.setAttribute("aria-label", field.getAttribute("aria-label"));
            button.setAttribute("aria-describedby", id + "-value");
        }
        // Validation and label clicks focus the hidden field: the button takes the focus instead.
        field.addEventListener("focus", function () { focus(button); });
        return button;
    }

    /** Wires a trigger to its field: display, disabled / invalid state, value changes from anywhere, opening. */
    function Picker(field, button, spec) {
        var baseDescribedBy = button.getAttribute("aria-describedby");

        function sync() {
            var text = spec.display(field.value);
            var span = button.querySelector(".th-trigger__value");
            span.textContent = text || field.getAttribute("data-empty-text") || spec.empty;
            span.classList.toggle("is-placeholder", !text);
            button.disabled = field.disabled;
            button.classList.toggle("is-readonly", field.readOnly);
            var invalid = field.classList.contains("input-validation-error") || field.getAttribute("aria-invalid") === "true";
            button.classList.toggle("is-invalid", invalid);
            if (invalid) button.setAttribute("aria-invalid", "true"); else button.removeAttribute("aria-invalid");
            var described = [baseDescribedBy, field.getAttribute("aria-describedby")].filter(Boolean).join(" ");
            if (described) button.setAttribute("aria-describedby", described); else button.removeAttribute("aria-describedby");
            if (field.required) button.setAttribute("aria-required", "true"); else button.removeAttribute("aria-required");
        }

        function set(value, keepOpen) {
            if (field.value !== value) {
                field.value = value;
                fire(field, "input");
                fire(field, "change");
                if (!value) {
                    // Clearing a date also clears a field that depends on it (e.g. the due time).
                    var linkedSel = field.getAttribute("data-clears");
                    var linked = linkedSel && (field.form || doc).querySelector(linkedSel);
                    if (linked && linked.value) { linked.value = ""; fire(linked, "change"); }
                }
                revalidate(field);
            }
            if (!keepOpen) Layer.close(true);
        }

        function open() {
            if (field.disabled || field.readOnly || Layer.isOpenFor(button)) return;
            var ui = spec.build({ field: field, set: set, close: function () { Layer.close(true); } });
            Layer.open(button, ui.el, {
                trapTab: !spec.list, matchWidth: !!spec.list, minWidth: 220,
                onClose: function () { button.setAttribute("aria-expanded", "false"); if (spec.onClose) spec.onClose(); }
            });
            button.setAttribute("aria-expanded", "true");
            ui.start();
            Layer.place();
        }

        button.addEventListener("click", function () { if (Layer.isOpenFor(button)) Layer.close(true); else open(); });
        button.addEventListener("keydown", function (e) {
            if (e.key === "ArrowDown" || e.key === "ArrowUp") { e.preventDefault(); open(); }
            else if ((e.key === "Delete" || e.key === "Backspace") && !field.required && !field.readOnly && field.value && !spec.list) { e.preventDefault(); set(""); }
        });

        hookValue(field, sync);
        field.addEventListener("change", sync);
        field.addEventListener("input", sync);
        if (window.MutationObserver) {
            new MutationObserver(sync).observe(field, {
                attributes: true,
                attributeFilter: ["disabled", "readonly", "required", "min", "max", "class", "aria-invalid", "aria-describedby", "data-empty-text"]
            });
        }
        field._thrydSync = sync;
        sync();
        return { sync: sync, set: set };
    }

    // ------------------------------------------------------------------ calendar (date picker and date-time picker)
    function Calendar(input, opts) {
        var el = doc.createElement("div");
        el.className = "th-cal";
        var min = parseIso(input.getAttribute("min")), max = parseIso(input.getAttribute("max"));
        var mode = "days", view = null, focusDay = null, anim = "";

        function selected() { return opts.value(); }
        function inRange(d) { return (!min || d >= min) && (!max || d <= max); }
        function clamp(d) { return min && d < min ? min : max && d > max ? max : d; }
        function monthOk(y, m) { return (!min || monthStart(y, m + 1) > min) && (!max || monthStart(y, m) <= max); }
        function yearOk(y) { return (!min || y >= min.getUTCFullYear()) && (!max || y <= max.getUTCFullYear()); }

        function head(title, label, prevOk, nextOk, prevLabel, nextLabel, titleMode) {
            return '<div class="th-cal__head">' +
                '<button type="button" class="th-iconbtn" data-move="-1" aria-label="' + prevLabel + '"' + (prevOk ? "" : " disabled") + ">" + icon("chevron-left", "th-flip") + "</button>" +
                '<button type="button" class="th-cal__title" ' + (titleMode ? 'data-mode="' + titleMode + '" aria-label="' + label + '"' : 'tabindex="-1" aria-disabled="true"') + ">" +
                '<span aria-live="polite">' + title + "</span>" + (titleMode ? icon("chevron-down") : "") + "</button>" +
                '<button type="button" class="th-iconbtn" data-move="1" aria-label="' + nextLabel + '"' + (nextOk ? "" : " disabled") + ">" + icon("chevron-right", "th-flip") + "</button></div>";
        }

        function renderDays() {
            var y = view.getUTCFullYear(), mo = view.getUTCMonth();
            var first = monthStart(y, mo);
            var start = addDays(first, -first.getUTCDay()); // weeks start on Sunday, like the school week
            var today = todayIso(), sel = selected();
            var html = head(MONTHS[mo] + " " + y, "Choose month and year, " + MONTHS[mo] + " " + y,
                !min || monthStart(y, mo) > min, !max || monthStart(y, mo + 1) <= max, "Previous month", "Next month", "months");
            html += '<div class="th-cal__body ' + anim + '"><div class="th-cal__grid" role="grid" aria-label="' + MONTHS[mo] + " " + y + '">' +
                '<div class="th-cal__row" role="row">';
            DAYS.forEach(function (d) { html += '<span class="th-cal__dow" role="columnheader" aria-label="' + d + '">' + d.slice(0, 2) + "</span>"; });
            html += "</div>";
            for (var w = 0; w < 6; w++) {
                html += '<div class="th-cal__row" role="row">';
                for (var i = 0; i < 7; i++) {
                    var day = addDays(start, w * 7 + i), key = iso(day), ok = inRange(day);
                    var cls = "th-cal__day" + (day.getUTCMonth() !== mo ? " is-outside" : "") + (key === today ? " is-today" : "") +
                        (key === sel ? " is-selected" : "") + (i === 5 || i === 6 ? " is-weekend" : "");
                    html += '<span role="gridcell"' + (key === sel ? ' aria-selected="true"' : "") + '><button type="button" class="' + cls + '" data-day="' + key + '" tabindex="-1"' +
                        ' aria-label="' + DAYS[day.getUTCDay()] + " " + day.getUTCDate() + " " + MONTHS[day.getUTCMonth()] + " " + day.getUTCFullYear() + (key === today ? ", today" : "") + (ok ? "" : ", not available") + '"' +
                        (key === sel ? ' aria-pressed="true"' : "") + (ok ? "" : " disabled") + ">" + day.getUTCDate() + "</button></span>";
                }
                html += "</div>";
            }
            return html + "</div></div>";
        }

        function renderMonths() {
            var y = view.getUTCFullYear(), sel = parseIso(selected()), today = parseIso(todayIso());
            var html = head(String(y), "Choose year, " + y, yearOk(y - 1), yearOk(y + 1), "Previous year", "Next year", "years");
            html += '<div class="th-cal__body ' + anim + '"><div class="th-cal__cells" role="grid" aria-label="Months of ' + y + '">';
            for (var m = 0; m < 12; m++) {
                var cls = "th-cal__cell" + (sel && sel.getUTCFullYear() === y && sel.getUTCMonth() === m ? " is-selected" : "") +
                    (today.getUTCFullYear() === y && today.getUTCMonth() === m ? " is-today" : "");
                html += '<button type="button" class="' + cls + '" data-month="' + m + '" tabindex="-1" aria-label="' + MONTHS[m] + " " + y + '"' +
                    (monthOk(y, m) ? "" : " disabled") + ">" + MONTHS[m].slice(0, 3) + "</button>";
            }
            return html + "</div></div>";
        }

        function renderYears() {
            var y0 = view.getUTCFullYear() - (view.getUTCFullYear() % 12), sel = parseIso(selected()), today = parseIso(todayIso());
            var html = head(y0 + " – " + (y0 + 11), "", yearOk(y0 - 1), yearOk(y0 + 12), "Previous years", "Next years", null);
            html += '<div class="th-cal__body ' + anim + '"><div class="th-cal__cells" role="grid" aria-label="Years ' + y0 + " to " + (y0 + 11) + '">';
            for (var y = y0; y < y0 + 12; y++) {
                var cls = "th-cal__cell" + (sel && sel.getUTCFullYear() === y ? " is-selected" : "") + (today.getUTCFullYear() === y ? " is-today" : "");
                html += '<button type="button" class="' + cls + '" data-year="' + y + '" tabindex="-1"' + (yearOk(y) ? "" : " disabled") + ">" + y + "</button>";
            }
            return html + "</div></div>";
        }

        function render(focusIt) {
            var html = mode === "days" ? renderDays() : mode === "months" ? renderMonths() : renderYears();
            if (!opts.embedded) {
                var t = parseIso(todayIso());
                html += '<div class="th-cal__foot"><button type="button" class="btn btn--ghost btn--sm" data-today' + (inRange(t) ? "" : " disabled") + ">Today</button>" +
                    (input.required ? "" : '<button type="button" class="btn btn--ghost btn--sm th-clear" data-clear>Clear</button>') + "</div>";
            }
            el.innerHTML = html;
            anim = "";
            var target;
            if (mode === "days") {
                target = el.querySelector('[data-day="' + iso(focusDay) + '"]:not([disabled])') ||
                    el.querySelector(".th-cal__day.is-selected:not([disabled])") || el.querySelector(".th-cal__day:not(.is-outside):not([disabled])");
            } else {
                var key = mode === "months" ? "data-month" : "data-year";
                var want = mode === "months" ? focusDay.getUTCMonth() : focusDay.getUTCFullYear();
                target = el.querySelector("[" + key + '="' + want + '"]:not([disabled])') || el.querySelector(".th-cal__cell:not([disabled])");
            }
            if (target) target.tabIndex = 0;
            if (focusIt && target) focus(target);
            Layer.schedule(); // the months / years views are shorter than the days view
            return target;
        }

        function go(d, dir) {
            d = clamp(d);
            focusDay = d;
            var nv = monthStart(d.getUTCFullYear(), d.getUTCMonth());
            if (nv.getTime() !== view.getTime()) { anim = dir < 0 ? "is-prev" : "is-next"; view = nv; }
            render(true);
        }

        function move(dir) {
            var y = view.getUTCFullYear(), m = view.getUTCMonth();
            if (mode === "days") view = monthStart(y, m + dir);
            else if (mode === "months") view = monthStart(y + dir, m);
            else view = monthStart(y + dir * 12, m);
            anim = dir < 0 ? "is-prev" : "is-next";
            focusDay = mode === "days" ? clamp(monthStart(view.getUTCFullYear(), view.getUTCMonth())) : view;
            var target = render(false);
            // The arrow that was pressed is a new element now: keep keyboard focus on it (or on the grid at the limit).
            var arrow = el.querySelector('[data-move="' + dir + '"]');
            focus(arrow && !arrow.disabled ? arrow : target);
        }

        el.addEventListener("click", function (e) {
            var b = e.target.closest("button");
            if (!b || b.disabled || b.getAttribute("aria-disabled") === "true") return;
            if (b.hasAttribute("data-move")) { move(+b.getAttribute("data-move")); return; }
            if (b.hasAttribute("data-mode")) { mode = b.getAttribute("data-mode"); anim = "is-zoom"; focusDay = view; render(true); return; }
            if (b.hasAttribute("data-month")) { view = monthStart(view.getUTCFullYear(), +b.getAttribute("data-month")); focusDay = clamp(view); mode = "days"; anim = "is-zoom"; render(true); return; }
            if (b.hasAttribute("data-year")) { view = monthStart(+b.getAttribute("data-year"), view.getUTCMonth()); focusDay = view; mode = "months"; anim = "is-zoom"; render(true); return; }
            if (b.hasAttribute("data-today")) {
                var t = parseIso(todayIso());
                if (inRange(t)) opts.pick(iso(t)); else { mode = "days"; go(t, 0); }
                return;
            }
            if (b.hasAttribute("data-clear")) { opts.pick(""); return; }
            if (b.hasAttribute("data-day")) {
                opts.pick(b.getAttribute("data-day"));
                if (opts.embedded) { focusDay = parseIso(b.getAttribute("data-day")); render(false); var f = el.querySelector(".th-cal__day.is-selected"); if (f) focus(f); }
            }
        });

        el.addEventListener("keydown", function (e) {
            var k = e.key;
            var day = e.target.getAttribute && e.target.getAttribute("data-day");
            if (day) {
                var d = parseIso(day), stepBy = { ArrowLeft: -1, ArrowRight: 1, ArrowUp: -7, ArrowDown: 7 }[k];
                if (stepBy && rtl(el) && (k === "ArrowLeft" || k === "ArrowRight")) stepBy = -stepBy;
                if (stepBy) { e.preventDefault(); go(addDays(d, stepBy), stepBy); }
                else if (k === "PageUp" || k === "PageDown") {
                    e.preventDefault();
                    var dir = k === "PageUp" ? -1 : 1;
                    var n = e.shiftKey ? 12 * dir : dir;
                    go(new Date(Date.UTC(d.getUTCFullYear(), d.getUTCMonth() + n, Math.min(d.getUTCDate(), 28))), dir);
                }
                else if (k === "Home") { e.preventDefault(); go(addDays(d, -d.getUTCDay()), -1); }
                else if (k === "End") { e.preventDefault(); go(addDays(d, 6 - d.getUTCDay()), 1); }
                return;
            }
            var cell = e.target.closest && e.target.closest(".th-cal__cell");
            if (cell) {
                var cells = $$(".th-cal__cell", el), i = cells.indexOf(cell);
                var by = { ArrowLeft: -1, ArrowRight: 1, ArrowUp: -3, ArrowDown: 3 }[k];
                if (by && rtl(el) && (k === "ArrowLeft" || k === "ArrowRight")) by = -by;
                if (by) {
                    e.preventDefault();
                    var next = cells[i + by];
                    if (next && !next.disabled) { cell.tabIndex = -1; next.tabIndex = 0; focus(next); }
                }
            }
        });

        return {
            el: el,
            start: function (focusIt) {
                var v = parseIso(selected()), t = parseIso(todayIso());
                focusDay = clamp(v || t);
                view = monthStart(focusDay.getUTCFullYear(), focusDay.getUTCMonth());
                mode = "days";
                return render(focusIt);
            },
            refresh: function () { render(false); }
        };
    }

    // ------------------------------------------------------------------ clock (time picker and date-time picker)
    function Clock(input, opts) {
        var el = doc.createElement("div");
        el.className = "th-time";
        var stepSec = parseFloat(input.getAttribute("step"));
        var stepMin = stepSec >= 60 ? Math.max(1, Math.round(stepSec / 60)) : 1;
        var lo = parseHm(input.getAttribute("min")), hi = parseHm(input.getAttribute("max"));
        // For a date-time field, min / max limit the time only on their own day.
        var dated = input.type === "datetime-local";
        var quick = (input.getAttribute("data-quick-times") || "").split(",").map(function (s) { return parseHm(s.trim()); }).filter(Boolean);
        var first = true;

        function minutesOf(t) { return t.h * 60 + t.m; }
        function limits() {
            if (!dated) return { lo: lo, hi: hi };
            var day = opts.day ? opts.day() : "";
            return {
                lo: lo && input.getAttribute("min").slice(0, 10) === day ? lo : null,
                hi: hi && input.getAttribute("max").slice(0, 10) === day ? hi : null
            };
        }
        function allowed(h, m) {
            if (m % stepMin !== 0) return false;
            var l = limits(), v = h * 60 + m;
            if (l.lo && l.hi && minutesOf(l.lo) > minutesOf(l.hi)) return v >= minutesOf(l.lo) || v <= minutesOf(l.hi); // overnight range
            return (!l.lo || v >= minutesOf(l.lo)) && (!l.hi || v <= minutesOf(l.hi));
        }
        function hourAllowed(h) { for (var m = 0; m < 60; m += stepMin) if (allowed(h, m)) return true; return false; }
        function nearestMinute(h, m) {
            if (allowed(h, m)) return m;
            for (var d = 1; d < 60; d++) {
                if (m + d < 60 && allowed(h, m + d)) return m + d;
                if (m - d >= 0 && allowed(h, m - d)) return m - d;
            }
            return null;
        }
        function current() { return parseHm(opts.value()); }

        function column(kind, title, entries) {
            var html = '<div class="th-time__colwrap"><span class="th-time__colhead" aria-hidden="true">' + title + "</span>" +
                '<div class="th-time__col" role="listbox" aria-label="' + title + '" data-col="' + kind + '">';
            entries.forEach(function (o) {
                html += '<button type="button" role="option" class="th-time__opt' + (o.selected ? " is-selected" : "") + '" data-' + kind + '="' + o.value + '"' +
                    ' aria-selected="' + !!o.selected + '"' + (o.label !== o.text ? ' aria-label="' + o.label + '"' : "") + (o.ok ? "" : " disabled") + ' tabindex="-1">' + o.text + "</button>";
            });
            return html + "</div></div>";
        }

        function render(focusKind) {
            var t = current();
            var pm = t ? t.h >= 12 : null;
            var hours = [], minutes = [];
            for (var h12 = 1; h12 <= 12; h12++) {
                var h24 = (h12 % 12) + (pm ? 12 : 0);
                hours.push({ value: h12, text: String(h12), label: h12 + " o'clock", selected: t && (t.h % 12 || 12) === h12, ok: t ? hourAllowed(h24) : hourAllowed(h12 % 12) || hourAllowed(h12 % 12 + 12) });
            }
            for (var m = 0; m < 60; m += stepMin) minutes.push({ value: m, text: pad(m), label: pad(m) + " minutes", selected: t && t.m === m, ok: t ? allowed(t.h, m) : true });
            if (t && t.m % stepMin !== 0) minutes.push({ value: t.m, text: pad(t.m), label: pad(t.m) + " minutes", selected: true, ok: true });
            minutes.sort(function (a, b) { return a.value - b.value; });
            var periodOk = function (isPm) { for (var h = isPm ? 12 : 0; h < (isPm ? 24 : 12); h++) if (hourAllowed(h)) return true; return false; };
            var periods = [
                { value: "AM", text: "AM", label: "AM", selected: pm === false, ok: periodOk(false) },
                { value: "PM", text: "PM", label: "PM", selected: pm === true, ok: periodOk(true) }
            ];

            var scroll = {};
            $$(".th-time__col", el).forEach(function (c) { scroll[c.getAttribute("data-col")] = c.scrollTop; });

            var html = "";
            if (!opts.embedded) {
                html += '<div class="th-time__head"><span class="th-time__now' + (t ? "" : " is-empty") + '" aria-live="polite">' + (t ? label12(t) : "No time chosen") + "</span></div>";
            }
            html += '<div class="th-time__cols">' + column("hour", "Hour", hours) + column("minute", "Min", minutes) + column("period", "", periods) + "</div>";
            if (quick.length) {
                html += '<div class="th-time__quick">' + quick.map(function (q) {
                    return '<button type="button" class="th-chip" data-quick="' + hm(q) + '"' + (allowed(q.h, q.m) ? "" : " disabled") + ">" + label12(q) + "</button>";
                }).join("") + "</div>";
            }
            if (!opts.embedded) {
                var now = parseHm(nowHm()), nowOk = now && hourAllowed(now.h) && nearestMinute(now.h, now.m) !== null;
                html += '<div class="th-cal__foot"><span class="th-foot__group"><button type="button" class="btn btn--ghost btn--sm" data-now' + (nowOk ? "" : " disabled") + ">Now</button>" +
                    (input.required ? "" : '<button type="button" class="btn btn--ghost btn--sm th-clear" data-clear>Clear</button>') + "</span>" +
                    '<button type="button" class="btn btn--primary btn--sm" data-done>Done</button></div>';
            }
            el.innerHTML = html;
            el.querySelector(".th-time__col[data-col=period]").parentNode.classList.add("th-time__colwrap--period");

            $$(".th-time__col", el).forEach(function (c) {
                var kind = c.getAttribute("data-col");
                var sel = c.querySelector(".is-selected");
                if (first && sel) keepVisible(c, sel, true);
                else if (scroll[kind] != null) c.scrollTop = scroll[kind];
                if (sel) keepVisible(c, sel); // the chosen value never sits out of view
                (sel || c.querySelector(".th-time__opt:not([disabled])") || {}).tabIndex = 0;
            });
            first = false;
            if (focusKind) {
                var target = el.querySelector('.th-time__col[data-col="' + focusKind + '"] .is-selected') || el.querySelector('.th-time__col[data-col="' + focusKind + '"] [tabindex="0"]');
                focus(target);
            }
            Layer.schedule();
        }

        function apply(t, focusKind) {
            opts.pick(t ? hm(t) : "");
            render(focusKind);
        }

        el.addEventListener("click", function (e) {
            var b = e.target.closest("button");
            if (!b || b.disabled) return;
            var t = current();
            if (b.hasAttribute("data-hour")) {
                var h12 = +b.getAttribute("data-hour");
                // No time yet: a school-day guess (7–11 morning; 12 and 1–6 afternoon) that AM / PM can change.
                var isPm = t ? t.h >= 12 : (h12 === 12 || h12 <= 6);
                var h = (h12 % 12) + (isPm ? 12 : 0);
                if (!hourAllowed(h)) h = (h12 % 12) + (isPm ? 0 : 12);
                var m = nearestMinute(h, t ? t.m : 0);
                if (m !== null) apply({ h: h, m: m }, "hour");
            } else if (b.hasAttribute("data-minute")) {
                var mm = +b.getAttribute("data-minute");
                var hh = t ? t.h : (function () { for (var x = 8; x < 32; x++) if (allowed(x % 24, mm)) return x % 24; return 12; })();
                apply({ h: hh, m: mm }, "minute");
            } else if (b.hasAttribute("data-period")) {
                var wantPm = b.getAttribute("data-period") === "PM";
                var base = t || { h: wantPm ? 12 : 8, m: 0 };
                var nh = (base.h % 12) + (wantPm ? 12 : 0);
                var nm = nearestMinute(nh, base.m);
                if (nm === null) { for (var y = wantPm ? 12 : 0; y < (wantPm ? 24 : 12); y++) if (hourAllowed(y)) { nh = y; nm = nearestMinute(y, 0); break; } }
                if (nm !== null) apply({ h: nh, m: nm }, "period");
            } else if (b.hasAttribute("data-quick")) {
                opts.pick(b.getAttribute("data-quick"));
                if (opts.embedded) render("hour"); else opts.close();
            } else if (b.hasAttribute("data-now")) {
                var n = parseHm(nowHm());
                var nmin = nearestMinute(n.h, n.m - (n.m % stepMin));
                if (nmin !== null) { opts.pick(hm({ h: n.h, m: nmin })); opts.close(); }
            } else if (b.hasAttribute("data-clear")) { opts.pick(""); opts.close(); }
            else if (b.hasAttribute("data-done")) opts.close();
        });

        el.addEventListener("keydown", function (e) {
            var opt = e.target.closest && e.target.closest(".th-time__opt");
            if (!opt) return;
            var col = opt.parentNode;
            var list = $$(".th-time__opt:not([disabled])", col), i = list.indexOf(opt);
            var k = e.key, horizontal = { ArrowRight: 1, ArrowLeft: -1 }[k];
            if (horizontal && rtl(el)) horizontal = -horizontal;
            if (k === "ArrowDown" || k === "ArrowUp" || k === "Home" || k === "End") {
                e.preventDefault();
                var n = k === "Home" ? list[0] : k === "End" ? list[list.length - 1] : list[i + (k === "ArrowDown" ? 1 : -1)];
                if (n) { opt.tabIndex = -1; n.tabIndex = 0; focus(n); keepVisible(col, n); }
            } else if (horizontal) {
                e.preventDefault();
                var cols = $$(".th-time__col", el), c = cols[cols.indexOf(col) + horizontal];
                if (c) focus(c.querySelector(".is-selected") || c.querySelector(".th-time__opt:not([disabled])"));
            }
        });

        return {
            el: el,
            start: function (focusIt) { first = true; render(focusIt ? "hour" : null); },
            refresh: function () { render(null); }
        };
    }

    // ------------------------------------------------------------------ date picker
    function initDate(input) {
        input._thryd = true;
        var button = makeTrigger(input, "calendar", "date", "dialog");
        Picker(input, button, {
            empty: "Choose a date",
            display: function (v) { var d = parseIso(v); return d ? longDate(d) : ""; },
            build: function (ctx) {
                var cal = Calendar(input, { value: function () { return input.value; }, pick: function (v) { ctx.set(v); } });
                var el = doc.createElement("div");
                el.className = "th-pop--cal";
                el.setAttribute("role", "dialog");
                el.setAttribute("aria-label", "Choose " + nameOf(input).toLowerCase());
                el.appendChild(cal.el);
                return { el: el, start: function () { cal.start(true); } };
            }
        });
    }

    // ------------------------------------------------------------------ time picker
    function initTime(input) {
        input._thryd = true;
        var button = makeTrigger(input, "clock", "time", "dialog");
        Picker(input, button, {
            empty: "Choose a time",
            display: function (v) { var t = parseHm(v); return t ? label12(t) : ""; },
            build: function (ctx) {
                var clock = Clock(input, { value: function () { return input.value; }, pick: function (v) { ctx.set(v, true); }, close: ctx.close });
                var el = doc.createElement("div");
                el.className = "th-pop--time";
                el.setAttribute("role", "dialog");
                el.setAttribute("aria-label", "Choose " + nameOf(input).toLowerCase());
                el.appendChild(clock.el);
                return { el: el, start: function () { clock.start(true); } };
            }
        });
    }

    // ------------------------------------------------------------------ date-time picker
    function initDateTime(input) {
        input._thryd = true;
        var button = makeTrigger(input, "calendar-days", "datetime", "dialog");
        var split = function () { var v = input.value || ""; return { d: v.slice(0, 10), t: v.length > 11 ? v.slice(11, 16) : "" }; };
        Picker(input, button, {
            empty: "Choose a date and time",
            display: function (v) {
                var d = parseIso(v), t = parseHm(v);
                return d ? longDate(d) + (t ? " · " + label12(t) : "") : "";
            },
            build: function (ctx) {
                var el = doc.createElement("div");
                el.className = "th-pop--datetime";
                el.setAttribute("role", "dialog");
                el.setAttribute("aria-label", "Choose " + nameOf(input).toLowerCase());
                var clock;
                var cal = Calendar(input, {
                    embedded: true,
                    value: function () { return split().d; },
                    pick: function (d) {
                        if (!d) return;
                        ctx.set(d + "T" + (split().t || "08:00"), true);
                        if (clock) clock.refresh();
                    }
                });
                clock = Clock(input, {
                    embedded: true,
                    day: function () { return split().d; },
                    value: function () { return split().t; },
                    pick: function (t) { if (t) ctx.set((split().d || todayIso()) + "T" + t, true); },
                    close: ctx.close
                });
                var wrap = doc.createElement("div");
                wrap.className = "th-dt";
                var timeBox = doc.createElement("div");
                timeBox.className = "th-dt__time";
                timeBox.innerHTML = '<p class="th-dt__label">Time</p>';
                timeBox.appendChild(clock.el);
                wrap.appendChild(cal.el);
                wrap.appendChild(timeBox);
                el.appendChild(wrap);
                var foot = doc.createElement("div");
                foot.className = "th-cal__foot";
                foot.innerHTML = '<span class="th-foot__group"><button type="button" class="btn btn--ghost btn--sm" data-now>Now</button>' +
                    (input.required ? "" : '<button type="button" class="btn btn--ghost btn--sm th-clear" data-clear>Clear</button>') + "</span>" +
                    '<button type="button" class="btn btn--primary btn--sm" data-done>Done</button>';
                el.appendChild(foot);
                foot.addEventListener("click", function (e) {
                    var b = e.target.closest("button");
                    if (!b) return;
                    if (b.hasAttribute("data-now")) ctx.set(todayIso() + "T" + nowHm());
                    else if (b.hasAttribute("data-clear")) ctx.set("");
                    else ctx.close();
                });
                return { el: el, start: function () { clock.start(false); cal.start(true); } };
            }
        });
    }

    // ------------------------------------------------------------------ multi-select
    function initMulti(select) {
        select._thryd = true;
        var button = makeTrigger(select, "list", "multi", "listbox");
        var before = null;
        var picker = Picker(select, button, {
            list: true,
            empty: "Choose…",
            display: function () {
                var chosen = Array.prototype.filter.call(select.options, function (o) { return o.selected; });
                if (!chosen.length) return "";
                if (chosen.length <= 2) return chosen.map(function (o) { return o.text; }).join(", ");
                return chosen[0].text + " +" + (chosen.length - 1) + " more";
            },
            build: function (ctx) {
                before = Array.prototype.map.call(select.options, function (o) { return o.selected; }).join();
                var ui = OptionList(select, {
                    multi: true,
                    search: wantsSearch(select),
                    onToggle: function () { picker.sync(); },
                    onDone: ctx.close
                });
                return { el: ui.panel, start: function () { ui.start({}); } };
            },
            onClose: function () {
                // One change event when the list closes, like a native multi-select after editing.
                var after = Array.prototype.map.call(select.options, function (o) { return o.selected; }).join();
                if (before !== null && after !== before) { fire(select, "input"); fire(select, "change"); revalidate(select); }
                before = null;
                picker.sync();
            }
        });
    }

    // ------------------------------------------------------------------ setup
    function skip(el) {
        return el._thryd || el.hasAttribute("data-native") || el.closest("[data-native]") || el.closest("template");
    }
    function setup(el) {
        if (skip(el)) return;
        if (el.nodeName === "SELECT") {
            if (el.multiple) initMulti(el);
            else if (el.size <= 1) initSelect(el);
            return;
        }
        var type = (el.getAttribute("type") || "").toLowerCase();
        if (type === "date") initDate(el);
        else if (type === "time") initTime(el);
        else if (type === "datetime-local") initDateTime(el);
    }
    var SELECTOR = "select, input[type=date], input[type=time], input[type=datetime-local]";

    function enhance(root) {
        root = root || doc;
        if (root.nodeType === 1 && root.matches(SELECTOR)) setup(root);
        if (root.querySelectorAll) $$(SELECTOR, root).forEach(setup);
    }

    // form.reset() does not go through .value: show the restored values.
    doc.addEventListener("reset", function (e) {
        var form = e.target;
        setTimeout(function () { $$(".th-native", form).forEach(function (f) { if (f._thrydSync) f._thrydSync(); }); }, 0);
    }, true);

    // New HTML from anywhere (AJAX lists, dialogs, cloned templates) gets the same controls, without each page asking.
    if (window.MutationObserver) {
        var pending = [];
        var flush = function () {
            var nodes = pending;
            pending = [];
            nodes.forEach(function (n) { if (n.isConnected) enhance(n); });
        };
        new MutationObserver(function (records) {
            records.forEach(function (r) {
                Array.prototype.forEach.call(r.addedNodes, function (n) {
                    if (n.nodeType === 1 && !n.classList.contains("th-pop") && (n.matches(SELECTOR) || n.querySelector(SELECTOR))) {
                        if (!pending.length) Promise.resolve().then(flush);
                        pending.push(n);
                    }
                });
            });
        }).observe(doc.documentElement, { childList: true, subtree: true });
    }

    window.ThrydUI = {
        enhance: enhance,
        close: function () { Layer.close(false, true); },
        sync: function (field) { if (field && field._thrydSync) field._thrydSync(); }
    };

    if (doc.readyState === "loading") doc.addEventListener("DOMContentLoaded", function () { enhance(doc); });
    else enhance(doc);
})();
