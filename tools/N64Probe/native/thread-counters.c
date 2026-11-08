/* Linux diagnostic helper: current-thread, user-space PMU counters.
 * Not linked into the emulator or ordinary probe builds. */
#define _GNU_SOURCE
#include <linux/perf_event.h>
#include <sys/syscall.h>
#include <sys/ioctl.h>
#include <unistd.h>
#include <stdint.h>
#include <string.h>
static __thread int leader=-1, member=-1;
struct counts { uint64_t nr, enabled, running, cycles, instructions; };
static int event(uint64_t config, int group) {
 struct perf_event_attr a; memset(&a,0,sizeof(a)); a.type=PERF_TYPE_HARDWARE; a.size=sizeof(a); a.config=config;
 a.disabled=group<0; a.exclude_kernel=1; a.exclude_hv=1;
 a.read_format=PERF_FORMAT_GROUP|PERF_FORMAT_TOTAL_TIME_ENABLED|PERF_FORMAT_TOTAL_TIME_RUNNING;
 return syscall(SYS_perf_event_open,&a,0,-1,group,0);
}
int counters_start(void) {
 leader=event(PERF_COUNT_HW_CPU_CYCLES,-1); if(leader<0)return -1;
 member=event(PERF_COUNT_HW_INSTRUCTIONS,leader); if(member<0){close(leader);leader=-1;return -1;}
 return ioctl(leader,PERF_EVENT_IOC_RESET,PERF_IOC_FLAG_GROUP)||ioctl(leader,PERF_EVENT_IOC_ENABLE,PERF_IOC_FLAG_GROUP);
}
int counters_read(struct counts *out,int stop) {
 if(leader<0)return -1;
 if(stop && ioctl(leader,PERF_EVENT_IOC_DISABLE,PERF_IOC_FLAG_GROUP))return -1;
 int ok=read(leader,out,sizeof(*out))==(ssize_t)sizeof(*out);
 if(stop){close(member);close(leader);leader=member=-1;}
 return ok?0:-1;
}
