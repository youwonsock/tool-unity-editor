#import <Foundation/Foundation.h>
#import <Metal/Metal.h>
#include "IUnityGraphics.h"
#include "IUnityGraphicsMetal.h"
#include <atomic>
#include <mutex>
#include <deque>

struct OTResult { int frame; int status; double milliseconds; double start; double end; };
static IUnityInterfaces* interfaces = nullptr;
static IUnityGraphicsMetalV2* metal = nullptr;
static std::atomic<bool> enabled(false);
static std::mutex gate;
static std::deque<OTResult> results;
static id<MTLCommandBuffer> beginBuffer;
static std::atomic<int> beginFrame(0);
static std::atomic<int> eventCalls(0), endCalls(0);
extern "C" void UNITY_INTERFACE_EXPORT OT_Stats(int* events, int* ends, int* frame) { *events=eventCalls; *ends=endCalls; *frame=beginFrame; }

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginLoad(IUnityInterfaces* value) { interfaces = value; }
extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginUnload() { enabled = false; beginBuffer = nil; }
extern "C" int UNITY_INTERFACE_EXPORT OT_Start() {
    metal = interfaces ? interfaces->Get<IUnityGraphicsMetalV2>() : nullptr;
    if (!metal || !metal->MetalDevice()) return 0;
    std::lock_guard<std::mutex> lock(gate); results.clear(); beginFrame = 0; enabled = true; return 1;
}
extern "C" void UNITY_INTERFACE_EXPORT OT_Clear() { std::lock_guard<std::mutex> lock(gate); results.clear(); }
extern "C" void UNITY_INTERFACE_EXPORT OT_Stop() { enabled = false; }
extern "C" int UNITY_INTERFACE_EXPORT OT_Poll(OTResult* value) {
    std::lock_guard<std::mutex> lock(gate);
    if (results.empty()) return 0;
    *value = results.front(); results.pop_front(); return 1;
}
static void UNITY_INTERFACE_API Event(int eventId) {
    @autoreleasepool {
        eventCalls++;
        if (!enabled || !metal) return;
        if (eventId == 1) {
            metal->EndCurrentCommandEncoder();
            beginBuffer = metal->CurrentCommandBuffer(); beginFrame++;
        } else if (eventId == 2) {
            endCalls++;
            const int frame = beginFrame;
            metal->EndCurrentCommandEncoder();
            id<MTLCommandBuffer> buffer = metal->CurrentCommandBuffer();
            if (!buffer) { std::lock_guard<std::mutex> lock(gate); results.push_back({frame,4,0,0,0}); return; }
            const int matched = buffer && buffer == beginBuffer;
            [buffer addCompletedHandler:^(id<MTLCommandBuffer> completed) {
                if (!enabled) return;
                OTResult result = {frame, matched ? 0 : 1, 0, completed.GPUStartTime, completed.GPUEndTime};
                if (completed.status != MTLCommandBufferStatusCompleted || result.start <= 0 || result.end <= result.start) result.status = 2;
                result.milliseconds = (result.end - result.start) * 1000.0;
                std::lock_guard<std::mutex> lock(gate);
                if (enabled) { if (results.size() >= 8192) { results.pop_front(); result.status = 3; } results.push_back(result); }
            }];
            metal->CommitCurrentCommandBuffer(); beginBuffer = nil;
        }
    }
}
extern "C" UnityRenderingEvent UNITY_INTERFACE_EXPORT OT_GetEvent() { return Event; }
