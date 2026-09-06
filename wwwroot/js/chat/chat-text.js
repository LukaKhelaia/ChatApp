// ---------------------------------------------------------------------------
//  ChatApp - chat-text.js
//
//  Message text, and turning the links in it into real links without ever
//  handing what somebody typed to innerHTML.
//
//  An ES module. Nothing here is global: what this file needs, it imports, and
//  what other files need from it, it exports. There is no load order to keep in
//  your head - chat-boot.js is the single entry and the module graph decides
//  the rest.
//
//  Two rules this file lives under:
//
//  chat-core.js imports NOTHING. It owns `connection`, and a cycle back into it
//  lets another module reach that const while it is still in its dead zone -
//  which is exactly how this refactor failed the first time.
//
//  Shared mutable state lives in chat-state.js, because an imported binding is
//  read-only at the importing end. If two files write the same value, it
//  belongs on that object, not in a `let` here.
// ---------------------------------------------------------------------------

    // ======================================================================
    //  LINKS IN MESSAGES
    // ======================================================================
    //  Message text has always gone in with textContent, which is why nothing
    //  a person types can become markup. Links have to keep that property: the
    //  text is still never handed to innerHTML, it is split into text nodes
    //  and anchors built with createElement, and the href is vetted rather
    //  than trusted. A message reading <img onerror=...> is still, after this,
    //  a message reading <img onerror=...>.

    // Deliberately not a URL grammar - it finds something link-shaped and lets
    // new URL() be the actual arbiter below.
    export const LINK_PATTERN = /(?:https?:\/\/|www\.)[^\s]+/gi;

    // Trailing punctuation belongs to the sentence, not the link: "see
    // https://example.com." must not swallow the full stop. A closing bracket
    // is kept only when the link opened one itself, so a wiki URL with
    // brackets in it survives while "(see https://example.com)" does not eat
    // the bracket that closes the aside.
    export function trimTrailingPunctuation(url) {
        const PAIRS = { ")": "(", "]": "[", "}": "{" };
        let end = url.length;

        while (end > 0) {
            const ch = url[end - 1];

            if (".,;:!?\u2019'\"".indexOf(ch) !== -1) { end--; continue; }

            const opener = PAIRS[ch];
            if (opener) {
                const slice = url.slice(0, end);
                let opens = 0, closes = 0;
                for (const c of slice) {
                    if (c === opener) opens++;
                    else if (c === ch) closes++;
                }
                if (closes > opens) { end--; continue; }
            }

            break;
        }

        return url.slice(0, end);
    }

    // Returns a safe absolute href, or null. Anything that is not plain http
    // or https is refused - javascript:, data: and friends never become a
    // clickable anchor, whatever the text around them looks like.
    export function safeHref(raw) {
        const candidate = /^https?:\/\//i.test(raw) ? raw : "https://" + raw;
        try {
            const url = new URL(candidate);
            return (url.protocol === "http:" || url.protocol === "https:") ? url.href : null;
        } catch (err) {
            return null;
        }
    }

    export function writeMessageText(el, text) {
        const pattern = new RegExp(LINK_PATTERN.source, "gi");
        let cursor = 0;
        let match;

        while ((match = pattern.exec(text)) !== null) {
            const start = match.index;
            const found = trimTrailingPunctuation(match[0]);

            // Trimming shortened the match, so the scan resumes at the end of
            // what we actually took - the punctuation we gave back is ordinary
            // text and has to be seen again.
            if (!found) { pattern.lastIndex = start + match[0].length; continue; }
            pattern.lastIndex = start + found.length;

            const href = safeHref(found);
            if (!href) continue;      // left in the stream as plain text

            if (start > cursor) el.appendChild(document.createTextNode(text.slice(cursor, start)));

            const a = document.createElement("a");
            a.className = "message-link";
            a.href = href;
            a.target = "_blank";
            // noopener keeps the opened page from reaching back through
            // window.opener; noreferrer keeps our URL out of its logs
            a.rel = "noopener noreferrer nofollow";
            a.textContent = found;   // what they typed, not the normalised href

            // The bubble toggles its timestamp on click. Following a link is
            // not that.
            a.addEventListener("click", (e) => e.stopPropagation());

            el.appendChild(a);
            cursor = start + found.length;
        }

        if (cursor < text.length) el.appendChild(document.createTextNode(text.slice(cursor)));
    }

