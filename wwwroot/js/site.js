// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Write your JavaScript code.

// ---------------------------------------------------------------------------
//  Modals
//
//  The edit forms used to unfold underneath the card they belonged to, which
//  pushed the whole page - and the footer - around every time one opened.
//  They are dialogs now: centred over a dimmed page, fading and rising into
//  place, and taking no space in the layout at all.
//
//  Shared by the profile edit form and both settings panels.
// ---------------------------------------------------------------------------
(function () {
    const FADE_MS = 260;          // keep in step with the CSS transition

    function dialogOf(overlay) {
        return overlay.querySelector(".app-modal-dialog");
    }

    // ----------------------------------------------------------------------
    //  Staying clear of the on-screen keyboard
    //
    //  position:fixed is measured against the LAYOUT viewport, which does not
    //  shrink when a phone keyboard slides up - so a dialog centred the plain
    //  way ends up centred behind the keyboard, which is exactly where the
    //  field you are typing into lives. visualViewport reports the part of the
    //  page actually on screen; pinning the overlay to that keeps the dialog
    //  centred in what is left above the keyboard.
    // ----------------------------------------------------------------------
    const vv = window.visualViewport;

    function fitToVisibleArea() {
        const overlay = document.querySelector(".app-modal.open");
        if (!overlay || !vv) return;

        overlay.style.top = vv.offsetTop + "px";
        overlay.style.left = vv.offsetLeft + "px";
        overlay.style.width = vv.width + "px";
        overlay.style.height = vv.height + "px";
        // inset:0 sets all four sides; with a width and a height as well the
        // box would be over-constrained, so hand the far edges back
        overlay.style.right = "auto";
        overlay.style.bottom = "auto";
    }

    function releaseVisibleArea(overlay) {
        overlay.style.top = overlay.style.left = "";
        overlay.style.width = overlay.style.height = "";
        overlay.style.right = overlay.style.bottom = "";
    }

    if (vv) {
        // the keyboard opening fires resize; scrolling the shrunken viewport
        // (iOS lifts the page rather than shortening it) fires scroll
        vv.addEventListener("resize", fitToVisibleArea);
        vv.addEventListener("scroll", fitToVisibleArea);
    }

    window.openModal = function (overlay) {
        if (!overlay || !overlay.hidden === false && overlay.classList.contains("open")) return;

        overlay.hidden = false;
        // one frame at the closed state, or the transition has nothing to
        // move from and the dialog simply appears
        void overlay.offsetHeight;
        overlay.classList.add("open");
        fitToVisibleArea();

        // the page behind must not scroll under the dialog
        document.body.classList.add("modal-open");

        const field = overlay.querySelector("input:not([type=hidden]), textarea, button");
        if (field) field.focus({ preventScroll: true });

        // focusing the field is what summons the keyboard, and the viewport
        // resize that follows can land before or after this frame
        requestAnimationFrame(fitToVisibleArea);
    };

    window.closeModal = function (overlay) {
        if (!overlay || overlay.hidden) return;

        overlay.classList.remove("open");
        document.body.classList.remove("modal-open");
        releaseVisibleArea(overlay);

        let settled = false;
        const done = () => {
            if (settled) return;
            settled = true;
            overlay.hidden = true;
        };

        // transitionend can be missed (reduced motion, a background tab), so
        // the timer is the guarantee rather than the fallback
        const dialog = dialogOf(overlay);
        if (dialog) dialog.addEventListener("transitionend", done, { once: true });
        setTimeout(done, FADE_MS + 60);
    };

    // Clicking the dimmed area, or Escape, closes whatever is open. Bound once
    // for every modal on the page rather than per dialog.
    document.addEventListener("click", (e) => {
        const overlay = e.target.classList && e.target.classList.contains("app-modal") ? e.target : null;
        if (overlay) closeModal(overlay);
    });

    document.addEventListener("keydown", (e) => {
        if (e.key !== "Escape") return;
        document.querySelectorAll(".app-modal.open").forEach(overlay => closeModal(overlay));
    });
})();

// ---------------------------------------------------------------------------
//  Six-digit code boxes
//
//  One box per digit, filling left to right, with the border turning pink as
//  each lands. Backspace walks back, arrows move, and pasting a whole code
//  fills the row.
//
//  The markup keeps a hidden input holding the joined value, so the pages that
//  read the code do not care that it is six fields rather than one:
//
//      <div class="code-boxes" data-code-for="emailCode"></div>
//      <input type="hidden" id="emailCode" />
// ---------------------------------------------------------------------------
(function () {
    const LENGTH = 6;
    const registry = new Map();          // hidden input id -> its box elements

    function boxesOf(hidden) {
        if (!hidden) return null;
        const id = typeof hidden === "string" ? hidden : hidden.id;
        return registry.get(id) || null;
    }

    function sync(hiddenId) {
        const boxes = registry.get(hiddenId);
        const hidden = document.getElementById(hiddenId);
        if (!boxes || !hidden) return;

        hidden.value = boxes.map(b => b.value).join("");
        boxes.forEach(b => b.classList.toggle("filled", b.value !== ""));
        hidden.dispatchEvent(new Event("input", { bubbles: true }));
    }

    function build(container) {
        const hiddenId = container.dataset.codeFor;
        if (!hiddenId || registry.has(hiddenId)) return;

        const boxes = [];

        for (let i = 0; i < LENGTH; i++) {
            const box = document.createElement("input");
            box.type = "text";
            box.className = "code-box";
            box.inputMode = "numeric";
            box.autocomplete = i === 0 ? "one-time-code" : "off";
            box.maxLength = 1;
            box.setAttribute("aria-label", "Digit " + (i + 1));
            container.appendChild(box);
            boxes.push(box);
        }

        registry.set(hiddenId, boxes);

        boxes.forEach((box, index) => {
            box.addEventListener("input", () => {
                // A phone keyboard can deliver more than one character, and so
                // can an autofilled code, so take the digits and spread them.
                const digits = box.value.replace(/\D/g, "");

                if (digits.length > 1) {
                    spread(hiddenId, digits, index);
                    return;
                }

                box.value = digits;
                if (digits && index < LENGTH - 1) boxes[index + 1].focus();
                sync(hiddenId);
            });

            box.addEventListener("keydown", (e) => {
                if (e.key === "Backspace") {
                    if (box.value) {
                        box.value = "";
                        sync(hiddenId);
                        return;
                    }
                    // already empty: step back and clear that one instead
                    if (index > 0) {
                        e.preventDefault();
                        boxes[index - 1].value = "";
                        boxes[index - 1].focus();
                        sync(hiddenId);
                    }
                    return;
                }

                if (e.key === "ArrowLeft" && index > 0) {
                    e.preventDefault();
                    boxes[index - 1].focus();
                }
                if (e.key === "ArrowRight" && index < LENGTH - 1) {
                    e.preventDefault();
                    boxes[index + 1].focus();
                }
            });

            box.addEventListener("paste", (e) => {
                const text = (e.clipboardData || window.clipboardData).getData("text") || "";
                const digits = text.replace(/\D/g, "");
                if (!digits) return;
                e.preventDefault();
                spread(hiddenId, digits, index);
            });

            // clicking a half-filled row should land where typing continues
            box.addEventListener("focus", () => box.select());
        });
    }

    function spread(hiddenId, digits, from) {
        const boxes = registry.get(hiddenId);
        if (!boxes) return;

        for (let i = 0; i < digits.length && from + i < LENGTH; i++) {
            boxes[from + i].value = digits[i];
        }

        const landed = Math.min(from + digits.length, LENGTH - 1);
        boxes[landed].focus();
        sync(hiddenId);
    }

    function containerOf(hidden) {
        const id = typeof hidden === "string" ? hidden : hidden.id;
        return document.querySelector('.code-boxes[data-code-for="' + id + '"]');
    }

    window.codeBoxes = {
        /// Shakes the row red, empties it, and puts the cursor back at the
        /// start - the code was wrong, so the next thing anyone does is type
        /// another one.
        error(hidden) {
            const container = containerOf(hidden);
            if (!container) return;

            container.classList.remove("error");
            void container.offsetWidth;          // restart the animation
            container.classList.add("error");

            const clear = () => {
                container.classList.remove("error");
                window.codeBoxes.clear(hidden);
                window.codeBoxes.focus(hidden);
            };

            container.addEventListener("animationend", clear, { once: true });
            setTimeout(clear, 600);              // in case the animation never runs
        },

        focus(hidden) {
            const boxes = boxesOf(hidden);
            if (!boxes) return;
            // the first empty box, or the last one if the row is full
            const next = boxes.find(b => !b.value) || boxes[LENGTH - 1];
            next.focus();
        },

        clear(hidden) {
            const boxes = boxesOf(hidden);
            if (!boxes) return;
            boxes.forEach(b => { b.value = ""; b.classList.remove("filled"); });
            const id = typeof hidden === "string" ? hidden : hidden.id;
            sync(id);
        }
    };

    function init() {
        document.querySelectorAll(".code-boxes").forEach(build);
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", init);
    } else {
        init();
    }
})();
