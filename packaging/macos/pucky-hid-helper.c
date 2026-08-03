#include <CoreFoundation/CoreFoundation.h>
#include <dlfcn.h>
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

enum { REPORT_LENGTH = 15 };

/*
 * IOHIDUserDevice is entitlement-gated and its header is not present in every
 * public macOS SDK. Resolve the stable framework symbols at runtime so the
 * helper can be built using the ordinary Command Line Tools SDK.
 */
typedef CFTypeRef (*create_user_device_fn)(CFAllocatorRef, CFDictionaryRef);
typedef int32_t (*handle_report_fn)(CFTypeRef, const uint8_t *, CFIndex);

static create_user_device_fn create_user_device;
static handle_report_fn handle_report;

/*
 * Standards-based gamepad descriptor matching Chromium's macOS Xbox Series X
 * Bluetooth mapping: 16 buttons, one hat, and six signed 16-bit axes. There
 * are no report IDs, so each input packet is exactly REPORT_LENGTH bytes.
 */
static const uint8_t report_descriptor[] = {
    0x05, 0x01,       /* Usage Page (Generic Desktop) */
    0x09, 0x05,       /* Usage (Game Pad) */
    0xA1, 0x01,       /* Collection (Application) */
    0x05, 0x09,       /*   Usage Page (Button) */
    0x19, 0x01,       /*   Usage Minimum (Button 1) */
    0x29, 0x10,       /*   Usage Maximum (Button 16) */
    0x15, 0x00,       /*   Logical Minimum (0) */
    0x25, 0x01,       /*   Logical Maximum (1) */
    0x75, 0x01,       /*   Report Size (1) */
    0x95, 0x10,       /*   Report Count (16) */
    0x81, 0x02,       /*   Input (Data, Variable, Absolute) */
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
    0x09, 0x33,       /*   Usage (Rx: left trigger) */
    0x09, 0x34,       /*   Usage (Ry: right trigger) */
    0x09, 0x35,       /*   Usage (Rz: right stick Y) */
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

static void *load_iokit(void)
{
    void *framework = dlopen(
        "/System/Library/Frameworks/IOKit.framework/IOKit",
        RTLD_NOW | RTLD_LOCAL);
    if (framework == NULL) {
        return NULL;
    }

    create_user_device = (create_user_device_fn)dlsym(framework, "IOHIDUserDeviceCreate");
    handle_report = (handle_report_fn)dlsym(framework, "IOHIDUserDeviceHandleReport");
    if (create_user_device == NULL || handle_report == NULL) {
        dlclose(framework);
        create_user_device = NULL;
        handle_report = NULL;
        return NULL;
    }
    return framework;
}

static CFTypeRef create_gamepad(void)
{
    CFMutableDictionaryRef properties = CFDictionaryCreateMutable(
        kCFAllocatorDefault,
        0,
        &kCFTypeDictionaryKeyCallBacks,
        &kCFTypeDictionaryValueCallBacks);
    if (properties == NULL) {
        return NULL;
    }

    set_number(properties, CFSTR("VendorID"), 0x045E);
    set_number(properties, CFSTR("ProductID"), 0x0B13);
    set_number(properties, CFSTR("VersionNumber"), 0x0509);
    set_number(properties, CFSTR("PrimaryUsagePage"), 0x01);
    set_number(properties, CFSTR("PrimaryUsage"), 0x05);
    CFDictionarySetValue(properties, CFSTR("Manufacturer"), CFSTR("Microsoft"));
    CFDictionarySetValue(properties, CFSTR("Product"), CFSTR("Xbox Wireless Controller"));
    CFDictionarySetValue(properties, CFSTR("SerialNumber"), CFSTR("PUCKY-VIRTUAL-1"));
    CFDictionarySetValue(properties, CFSTR("Transport"), CFSTR("Bluetooth"));

    CFDataRef descriptor = CFDataCreate(
        kCFAllocatorDefault,
        report_descriptor,
        (CFIndex)sizeof(report_descriptor));
    if (descriptor == NULL) {
        CFRelease(properties);
        return NULL;
    }
    CFDictionarySetValue(properties, CFSTR("ReportDescriptor"), descriptor);
    CFRelease(descriptor);

    CFTypeRef device = create_user_device(kCFAllocatorDefault, properties);
    CFRelease(properties);
    return device;
}

int main(void)
{
    signal(SIGPIPE, SIG_IGN);
    setvbuf(stdout, NULL, _IONBF, 0);

    void *iokit = load_iokit();
    if (iokit == NULL) {
        fputs("ERROR Could not load IOHIDUserDevice symbols from IOKit.\n", stdout);
        return 1;
    }

    CFTypeRef device = create_gamepad();
    if (device == NULL) {
        fputs("ERROR macOS refused to create the virtual HID device. Check the helper signature, entitlement, and AMFI policy.\n", stdout);
        dlclose(iokit);
        return 2;
    }

    fprintf(stdout, "READY %d\n", REPORT_LENGTH);
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
                dlclose(iokit);
                return 3;
            }
            continue;
        }

        used += count;
        if (used == REPORT_LENGTH) {
            int32_t result = handle_report(device, report, REPORT_LENGTH);
            if (result != 0) {
                fprintf(
                    stderr,
                    "IOHIDUserDeviceHandleReport failed: 0x%08x\n",
                    (unsigned int)result);
            }
            used = 0;
        }
    }

    CFRelease(device);
    dlclose(iokit);
    return 0;
}
