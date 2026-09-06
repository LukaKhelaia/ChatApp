// ---------------------------------------------------------------------------
//  ChatApp - chat-state.js
//
//  The page's mutable state, in one object.
//
//  Not a stylistic choice - the module system requires it. An imported binding
//  is read-only at the importing end: you can read `selectedUser` from another
//  module and see it change, but `selectedUser = x` there is a compile error.
//  These twelve are all written from more than one file, so they cannot be
//  plain exported `let`s.
//
//  A single exported object sidesteps that: the binding never changes, only
//  its properties do, and every file sees the same object. It also makes the
//  shared mutable state of this page something you can read in one screen,
//  which it very much was not when all of it lived in the global scope.
//
//  Everything that ISN'T written from more than one file stays where it lives.
//  This is not a dumping ground for state - if a variable belongs to one file,
//  leave it there.
// ---------------------------------------------------------------------------

export const state = {
    // --- which conversation is open ---------------------------------------
    /** Lower-cased user name of the private chat on screen, or null. */
    selectedUser: null,
    /** Id of the group chat on screen, or null. Never both at once. */
    selectedGroupId: null,
    /** Their face and name, kept for the "seen by" avatar, which needs them
        outside the header. */
    selectedUserAvatar: null,
    selectedUserNickname: null,

    // --- what we know about the open group ---------------------------------
    groupCreatorUserName: null,
    /** Creator, or someone the creator appointed. Decides whether "Add
        members" shows and whether member rows offer a Kick button. */
    groupIAmAdmin: false,
    /** Highest group message id seen, so a read receipt never goes backwards. */
    latestGroupMessageId: 0,

    // --- loading the open conversation -------------------------------------
    /** False while history is in flight; arrivals queue up until it is true. */
    isHistoryLoaded: false,
    /** Messages that landed while history was still loading. */
    messageQueue: [],

    // --- the composer -------------------------------------------------------
    /** The upload waiting to be sent with the next message, or null. */
    pendingAttachment: null,
    /** True while an upload is in flight, which disables send. */
    attachmentUploading: false,
    /** When we last told the other end we are typing, for the throttle. */
    lastTypingPing: 0
};
