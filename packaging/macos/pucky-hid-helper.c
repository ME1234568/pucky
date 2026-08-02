#include <CoreFoundation/CoreFoundation.h>
#include <IOKit/hid/IOHIDKeys.h>
#include <IOKit/hid/IOHIDUserDevice.h>
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

enum { REPORT_LENGTH = 15 };

/*
 * Standards-based gamepad descriptor matching SDL's macOS Razer Serval
 * mapping: 11 buttons, one hat, and six signed 16-bit axes. There are no
 * report IDs, so each input packet is exactly REPORT_LENGTH bytes.
 */
static const uint8_t report_descriptor[] = {
    0x05, 0x01,       /* Usage Page (Generic Desktop) */
    0x09, 0x05,       /* Usage (Game Pad) */
    0xA1, 0x01,       /* Collection (Application) */
    0x05, 0x09,       /*   Usage Page (Button) */
    0x19, 0x01,       /*   Usage Minimum (Button 1) */
    0x29, 0x0B,       /*   Usage Maximum (Button 11) */
    0x15, 0x00,       /*   Logical Minimum (0) */
    0x25, 0x01,       /*   Logical Maximum (1) */
    0x75, 0x01,       /*   Report Size (1) */
    0x95, 0x0B,       /*   Report Count (11) */
    0x81, 0x02,       /*   Input (Data, Variable, Absolute) */
    0x75, 0x01,       /*   Report Size (1) */
    0x95, 0x05,       /*   Report Count (5) */
    0x81, 0x03,       /*   Input (Constant) */
    0x05, 0x01,       /*   Usage Page (Generic Desktop) */
    0x09, 0x39,       /*   Usage (Hat Switch) */
    0x15, 0x00,       /*   Logical Minimum (0) */
    0x25, 0x07,       /*   Logical Maximum (7) */
    0x35, 0x00,       /*   Physical Minimum (0) */
    0x46, 0x3B, 0x01, /*   Physical Maximum (315) */
    0x65, 0x14,       /*   Unit (Degrees) */
    0x75, 0x04,       /*   Report Size (4) */
    0x95, 0x01,       /*   Report Count (1) */
    0x81, 0x42,       /*   Input (Data, Variable, Absolute, Null State) */
    0x65, 0x00,       /*   Unit (None) */
    0x75, 0x04,       /*   Report Size (4) */
    0x95, 0x01,       /*   Report Count (1) */
    0x81, 0x03,       /*   Input (Constant) */
    0x09, 0x30,       /*   Usage (X: left stick X) */
    0x09, 0x31,       /*   Usage (Y: left stick Y) */
    0x09, 0x32,       /*   Usage (Z: right stick X) */
    0x09, 0x33,       /*   Usage (Rx: right stick Y) */
    0x09, 0x34,       /*   Usage (Ry: right trigger) */
    0x09, 0x35,       /*   Usage (Rz: left trigger) */
    0x16, 0x00, 0x80, /*   Logical Minimum (-32768) */
    0x26, 0xFF, 0x7F, /*   Logical Maximum (32767) */
    0x75, 0x10,       /*   Report Size (16) */
    0x95, 0x06,       /*   Report Count (6) */
    0x81, 0x02,       /*   Input (Data, Variable, Absolute) */
    0xC0              /* End Collection */
};

static void set_number(CFMutableDictionaryRef properties, CFStringRef key, int32_t value)
{
    CFNumberRef number = CFNumberCreate(kCFAllocatorDefault, kCFNumberSInt32Type, &value);
    if (number != NULL) {
        CFDictionarySetValue(properties, key, number);
        CFRelease(number);
    }
}

static IOHIDUserDeviceRef create_gamepad(void)
{
    CFMutableDictionaryRef properties = CFDictionaryCreateMutable(
        kCFAllocatorDefault,
        0,
        &kCFTypeDictionaryKeyCallBacks,
        &kCFTypeDictionaryValueCallBacks);
    if (properties == NULL) {
        return NULL;
    }

    set_number(properties, CFSTR(kIOHIDVendorIDKey), 0x1532);
    set_number(properties, CFSTR(kIOHIDProductIDKey), 0x0900);
    set_number(properties, CFSTR(kIOHIDVersionNumberKey), 0x0200);
    set_number(properties, CFSTR(kIOHIDPrimaryUsagePageKey), 0x01);
    set_number(properties, CFSTR(kIOHIDPrimaryUsageKey), 0x05);
    CFDictionarySetValue(properties, CFSTR(kIOHIDManufacturerKey), CFSTR("Razer"));
    CFDictionarySetValue(properties, CFSTR(kIOHIDProductKey), CFSTR("Razer Serval"));
    CFDictionarySetValue(properties, CFSTR(kIOHIDSerialNumberKey), CFSTR("PUCKY-VIRTUAL-1"));
    CFDictionarySetValue(properties, CFSTR(kIOHIDTransportKey), CFSTR("USB"));

    CFDataRef descriptor = CFDataCreate(
        kCFAllocatorDefault,
        report_descriptor,
        (CFIndex)sizeof(report_descriptor));
    if (descriptor == NULL) {
        CFRelease(properties);
        return NULL;
    }
    CFDictionarySetValue(properties, CFSTR(kIOHIDReportDescriptorKey), descriptor);
    CFRelease(descriptor);

    IOHIDUserDeviceRef device = IOHIDUserDeviceCreate(kCFAllocatorDefault, properties);
    CFRelease(properties);
    return device;
}

int main(void)
{
    signal(SIGPIPE, SIG_IGN);
    setvbuf(stdout, NULL, _IONBF, 0);

    IOHIDUserDeviceRef device = create_gamepad();
    if (device == NULL) {
        fputs("ERROR macOS refused to create the virtual HID device. Check the helper signature, entitlement, and AMFI policy.\n", stdout);
        return 2;
    }

    fputs("READY\n", stdout);
    uint8_t report[REPORT_LENGTH];
    size_t used = 0;
    for (;;) {
        size_t count = fread(report + used, 1, REPORT_LENGTH - used, stdin);
        if (count == 0) {
            if (feof(stdin)) {
                break;
            }
            if (ferror(stdin)) {
                CFRelease(device);
                return 3;
            }
            continue;
        }

        used += count;
        if (used == REPORT_LENGTH) {
            IOReturn result = IOHIDUserDeviceHandleReport(device, report, REPORT_LENGTH);
            if (result != kIOReturnSuccess) {
                fprintf(
                    stderr,
                    "IOHIDUserDeviceHandleReport failed: 0x%08x\n",
                    (unsigned int)result);
            }
            used = 0;
        }
    }

    CFRelease(device);
    return 0;
}
