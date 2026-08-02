namespace Pucky.Core.Input;

[Flags]
public enum SteamButton : ulong
{
    None = 0,
    A = 1UL << 0,
    B = 1UL << 1,
    X = 1UL << 2,
    Y = 1UL << 3,
    QuickAccess = 1UL << 4,
    RightStick = 1UL << 5,
    Menu = 1UL << 6,
    R4 = 1UL << 7,
    R5 = 1UL << 8,
    RightBumper = 1UL << 9,
    DPadDown = 1UL << 10,
    DPadRight = 1UL << 11,
    DPadLeft = 1UL << 12,
    DPadUp = 1UL << 13,
    Menu = 1UL << 14,
    LeftStick = 1UL << 15,
    Steam = 1UL << 16,
    L4 = 1UL << 17,
    L5 = 1UL << 18,
    LeftBumper = 1UL << 19,
    RightStickTouch = 1UL << 20,
    RightPadTouch = 1UL << 21,
    RightPadClick = 1UL << 22,
    RightTriggerFull = 1UL << 23,
    LeftStickTouch = 1UL << 24,
    LeftPadTouch = 1UL << 25,
    LeftPadClick = 1UL << 26,
    LeftTriggerFull = 1UL << 27,
    RightGripTouch = 1UL << 28,
    LeftGripTouch = 1UL << 29
}
