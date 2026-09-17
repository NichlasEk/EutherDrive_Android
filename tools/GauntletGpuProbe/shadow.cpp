// Reuse the validated harness and capture-state validation. The CLI remains
// available separately; this library has a deliberately small synchronous ABI.
#define GAUNTLET_GPU_LIBRARY
#include "main.cpp"

namespace {
constexpr size_t pixels=2097152,metaAt=8+2097152+512,initialAt=metaAt+256;
constexpr size_t batchInitial=metaAt+128*256,capacity=64*1024*1024;
thread_local std::string lastError;
using ProfileClock=std::chrono::steady_clock;
struct ProfileTimer {
    double* sum;ProfileClock::time_point start;
    explicit ProfileTimer(double* target):sum(target),start(target?ProfileClock::now():ProfileClock::time_point{}) {}
    ~ProfileTimer() { if(sum) *sum+=std::chrono::duration<double,std::milli>(ProfileClock::now()-start).count(); }
};
struct Shadow {
    Harness gpu;
    std::vector<uint32_t> input=std::vector<uint32_t>(initialAt+2*pixels);
    bool initialized=false;
    bool residentDirty=false;
    uint64_t pixelReadbacks=0;
    uint64_t dispatchInvocations=0;
    uint64_t snapshotCopiedBytes=0;
    uint64_t pagesCompared=0,pagesSkipped=0;
    bool profiling=[] { auto p=std::getenv("EUTHERDRIVE_GAUNTDL_GPU_PROFILE");return p && std::strcmp(p,"1")==0; }();
    double initMs=0,prepareMs=0,recordMs=0,stagingMs=0,queueMs=0,waitMs=0,queryMs=0,readPixelsMs=0;
    double gpuPreMs=0,gpuDispatchMs=0,gpuPostMs=0,gpuPixelReadMs=0;
    double ticksMs(uint64_t start,uint64_t end) const {
        uint64_t mask=gpu.timestampBits==64?UINT64_MAX:(uint64_t(1)<<gpu.timestampBits)-1;
        return ((end-start)&mask)*gpu.properties.limits.timestampPeriod/1e6;
    }
    std::vector<uint32_t> batch;
    std::vector<std::array<uint32_t,4>> draws;
    std::vector<std::vector<VkBufferCopy>> updates;
    uint64_t submissions=0,uploaded=0,readback=0,patchBytes=0,queuedDraws=0;
    explicit Shadow(const char* shader) {
        ProfileTimer timer(profiling?&initMs:nullptr);
        input[0]=0x32524447;input[1]=2;input[2]=2097152;input[3]=512;
        input[4]=256;input[5]=pixels;
        gpu.init(load(shader),capacity,(pixels+16)*4);
        std::cout<<"gpuShadow device="<<gpu.properties.deviceName<<std::endl;
    }
    void submit(const std::vector<uint32_t>& data,size_t bytes,size_t initial,
        const std::vector<std::array<uint32_t,4>>& commands,
        const std::vector<std::vector<VkBufferCopy>>& patches,bool reset,bool statisticsOnly=false,
        const std::vector<VkBufferCopy>* sparseUploads=nullptr,uint32_t dispatchPixels=pixels) {
        if(bytes>capacity) throw std::runtime_error("Shadow batch capacity exceeded");
        auto& h=gpu;
        { ProfileTimer timer(profiling?&recordMs:nullptr);
          h.record(bytes,(pixels+16)*4,dispatchPixels,2,0,commands,initial,patches,reset,64,statisticsOnly,sparseUploads,profiling); }
        size_t transferred=bytes;
        { ProfileTimer timer(profiling?&stagingMs:nullptr);
        if(sparseUploads) {
            transferred=0;
            for(const auto& copy:*sparseUploads) {
                std::memcpy(static_cast<char*>(h.buffers[0].mapped)+copy.srcOffset,
                    reinterpret_cast<const char*>(data.data())+copy.srcOffset,copy.size);
                transferred+=copy.size;
            }
        } else std::memcpy(h.buffers[0].mapped,data.data(),bytes);
        }
        { ProfileTimer timer(profiling?&queueMs:nullptr);
        check(vkResetFences(h.device,1,&h.fence));
        VkSubmitInfo info{VK_STRUCTURE_TYPE_SUBMIT_INFO};info.commandBufferCount=1;info.pCommandBuffers=&h.commands;
        check(vkQueueSubmit(h.queue,1,&info,h.fence));
        }
        { ProfileTimer timer(profiling?&waitMs:nullptr);
          check(vkWaitForFences(h.device,1,&h.fence,VK_TRUE,UINT64_MAX)); }
        if(profiling) {
            ProfileTimer timer(&queryMs);uint64_t ticks[4];
            check(vkGetQueryPoolResults(h.device,h.queries,0,4,sizeof(ticks),ticks,sizeof(uint64_t),VK_QUERY_RESULT_64_BIT));
            gpuPreMs+=ticksMs(ticks[2],ticks[0]);gpuDispatchMs+=ticksMs(ticks[0],ticks[1]);gpuPostMs+=ticksMs(ticks[1],ticks[3]);
        }
        if(h.validationErrors) throw std::runtime_error("Vulkan shadow validation errors");
        submissions++;uploaded+=transferred;readback+=statisticsOnly?64:(pixels+16)*4;
        dispatchInvocations+=uint64_t((dispatchPixels+127)/128)*128*commands.size();
    }
};
}
extern "C" {
int gauntlet_shadow_abi_version() noexcept { return 4; }
const char* gauntlet_shadow_error() { return lastError.c_str(); }
void* gauntlet_shadow_create(const char* shader) noexcept {
    try { lastError.clear();return new Shadow(shader); }
    catch(const std::exception& e) { lastError=e.what();return nullptr; }
}
void gauntlet_shadow_destroy(void* context) noexcept {
    auto* s=static_cast<Shadow*>(context);
    if(s) std::cout<<"gpuShadowTotals submissions="<<s->submissions<<" uploadBytes="<<s->uploaded<<" readbackBytes="<<s->readback<<" patchBytes="<<s->patchBytes<<" queuedDraws="<<s->queuedDraws<<std::endl;
    if(s) std::cout<<"gpuResident pixelReadbacks="<<s->pixelReadbacks<<" pendingPixels="<<s->residentDirty<<std::endl;
    if(s) std::cout<<"gpuDispatch invocations="<<s->dispatchInvocations<<std::endl;
    if(s) std::cout<<"gpuSnapshot copiedBytes="<<s->snapshotCopiedBytes<<std::endl;
    if(s) std::cout<<"gpuDirty pagesCompared="<<s->pagesCompared<<" pagesSkipped="<<s->pagesSkipped<<std::endl;
    if(s && s->profiling) {
        std::cout<<"gpuProfileHost initMs="<<s->initMs<<" prepareMs="<<s->prepareMs<<" recordMs="<<s->recordMs<<" stagingMs="<<s->stagingMs<<" queueMs="<<s->queueMs<<" waitMs="<<s->waitMs<<" queryMs="<<s->queryMs<<" readPixelsMs="<<s->readPixelsMs<<std::endl;
        std::cout<<"gpuProfileDevice preDispatchMs="<<s->gpuPreMs<<" dispatchMs="<<s->gpuDispatchMs<<" postDispatchMs="<<s->gpuPostMs<<" pixelReadMs="<<s->gpuPixelReadMs<<std::endl;
    }
    if(s && s->gpu.messenger) std::cout<<"gpuShadow synchronizationValidationErrors="<<s->gpu.validationErrors.load()<<std::endl;
    delete s;
}
static int renderDraw(void* context,const uint32_t* texture,const uint32_t* ncc,
    const uint32_t* meta,const uint16_t* color,const uint16_t* depth,int reset,bool resident,const uint32_t* dirty=nullptr) noexcept {
    try {
        if(!context || !texture || !ncc || !meta || !color || !depth)
            throw std::runtime_error("Null shadow input");
        auto& s=*static_cast<Shadow*>(context);
        auto prepareStart=s.profiling?ProfileClock::now():ProfileClock::time_point{};
        if(s.residentDirty && (reset || !resident)) throw std::runtime_error("Resident framebuffer requires readback before reset/mode change");
        if(!s.draws.empty()) throw std::runtime_error("Flush queued draws before changing mode");
        if(resident && meta[31]!=1) throw std::runtime_error("Resident draw requires raster statistics");
        if(!s.initialized && !reset) throw std::runtime_error("Shadow requires initial framebuffer");
        // Compare 1 KiB pages against the last submitted snapshot. Reset always
        // uploads everything, including after CPU fallback or a mode change.
        const char* incrementalFlag=std::getenv("EUTHERDRIVE_GAUNTDL_GPU_INCREMENTAL");
        bool incremental=resident && !reset && incrementalFlag && std::strcmp(incrementalFlag,"1")==0;
        const char* sparseFlag=std::getenv("EUTHERDRIVE_GAUNTDL_GPU_SPARSE_SNAPSHOT");
        bool sparseSnapshot=incremental && sparseFlag && std::strcmp(sparseFlag,"1")==0;
        const char* verifyFlag=std::getenv("EUTHERDRIVE_GAUNTDL_GPU_VERIFY_DIRTY");
        bool verifyDirty=verifyFlag && std::strcmp(verifyFlag,"1")==0;
        if(dirty && !reset && !sparseSnapshot) throw std::runtime_error("Dirty draw requires incremental sparse snapshots");
        std::vector<VkBufferCopy> uploads;
        if(incremental) {
            for(size_t p=0;p<pixels+512;p+=256) {
                const uint32_t* now=p<pixels?texture+p:ncc+p-pixels;
                if(dirty && p<pixels && !dirty[p/256]) {
                    s.pagesSkipped++;
                    if(verifyDirty && !std::equal(now,now+256,s.input.begin()+8+p))
                        throw std::runtime_error("Dirty texture tracker missed page "+std::to_string(p/256));
                    continue;
                }
                s.pagesCompared++;
                if(std::equal(now,now+256,s.input.begin()+8+p)) continue;
                if(sparseSnapshot) {
                    std::copy_n(now,256,s.input.begin()+8+p);
                    s.snapshotCopiedBytes+=1024;
                }
                if(std::getenv("GAUNTLET_GPU_DROP_TEXTURE_UPDATES")) continue;
                VkBufferCopy copy{(8+p)*4,(8+p)*4,1024};
                if(!uploads.empty() && uploads.back().srcOffset+uploads.back().size==copy.srcOffset)
                    uploads.back().size+=copy.size;
                else uploads.push_back(copy);
                s.patchBytes+=1024;
            }
            uploads.push_back({metaAt*4,metaAt*4,256*4});
        }
        if(!sparseSnapshot) {
            std::copy_n(texture,2097152,s.input.begin()+8);
            std::copy_n(ncc,512,s.input.begin()+8+2097152);
            s.snapshotCopiedBytes+=(pixels+512)*4;
        }
        std::copy_n(meta,256,s.input.begin()+metaAt);
        validateCapture(s.input,true);
        if(reset) for(size_t i=0;i<pixels;i++) s.input[initialAt+i]=color[i]|uint32_t(depth[i])<<16;
        size_t bytes=(initialAt+(reset?pixels:0))*4;
        const char* bboxFlag=std::getenv("EUTHERDRIVE_GAUNTDL_GPU_BBOX");
        bool bbox=bboxFlag && std::strcmp(bboxFlag,"1")==0;
        // Physical output addressing is independent of invocation index in
        // GDR2. Keep data[5] unchanged: it also locates the statistics tail.
        if(bbox && meta[23]!=1) throw std::runtime_error("Bounding-box dispatch requires physical output");
        uint32_t dispatchPixels=bbox?meta[2]*meta[3]:pixels;
        std::array<uint32_t,4> command={uint32_t(metaAt),bbox?meta[0]:0,bbox?meta[1]:0,bbox?meta[2]:1024};
        if(s.profiling) s.prepareMs+=std::chrono::duration<double,std::milli>(ProfileClock::now()-prepareStart).count();
        s.submit(s.input,bytes,initialAt,{command},{{}},reset!=0,resident,incremental?&uploads:nullptr,dispatchPixels);
        s.residentDirty=resident;
        s.initialized=true;return 0;
    } catch(const std::exception& e) { lastError=e.what();return -1; }
}
int gauntlet_shadow_draw(void* context,const uint32_t* texture,const uint32_t* ncc,
    const uint32_t* meta,const uint16_t* color,const uint16_t* depth,int reset) noexcept {
    return renderDraw(context,texture,ncc,meta,color,depth,reset,false);
}
int gauntlet_shadow_resident_draw(void* context,const uint32_t* texture,const uint32_t* ncc,
    const uint32_t* meta,const uint16_t* color,const uint16_t* depth,int reset) noexcept {
    return renderDraw(context,texture,ncc,meta,color,depth,reset,true);
}
int gauntlet_shadow_dirty_draw(void* context,const uint32_t* texture,const uint32_t* ncc,
    const uint32_t* meta,const uint16_t* color,const uint16_t* depth,int reset,const uint32_t* dirty) noexcept {
    if(!dirty) { lastError="Null dirty mask";return -1; }
    return renderDraw(context,texture,ncc,meta,color,depth,reset,true,dirty);
}
int gauntlet_shadow_read_pixels(void* context) noexcept {
    try {
        if(!context) throw std::runtime_error("Null shadow context");
        auto& s=*static_cast<Shadow*>(context);auto& h=s.gpu;
        ProfileTimer timer(s.profiling?&s.readPixelsMs:nullptr);
        if(!s.residentDirty) throw std::runtime_error("No pending resident framebuffer");
        check(vkResetCommandBuffer(h.commands,0));
        VkCommandBufferBeginInfo begin{VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO};check(vkBeginCommandBuffer(h.commands,&begin));
        if(s.profiling) { vkCmdResetQueryPool(h.commands,h.queries,0,2);vkCmdWriteTimestamp(h.commands,VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT,h.queries,0); }
        VkMemoryBarrier ready{VK_STRUCTURE_TYPE_MEMORY_BARRIER};ready.srcAccessMask=VK_ACCESS_MEMORY_WRITE_BIT;ready.dstAccessMask=VK_ACCESS_TRANSFER_READ_BIT;
        vkCmdPipelineBarrier(h.commands,VK_PIPELINE_STAGE_ALL_COMMANDS_BIT,VK_PIPELINE_STAGE_TRANSFER_BIT,0,1,&ready,0,nullptr,0,nullptr);
        VkBufferCopy copy{0,0,pixels*4};vkCmdCopyBuffer(h.commands,h.buffers[2].buffer,h.buffers[3].buffer,1,&copy);
        VkMemoryBarrier host{VK_STRUCTURE_TYPE_MEMORY_BARRIER};host.srcAccessMask=VK_ACCESS_TRANSFER_WRITE_BIT;host.dstAccessMask=VK_ACCESS_HOST_READ_BIT;
        vkCmdPipelineBarrier(h.commands,VK_PIPELINE_STAGE_TRANSFER_BIT,VK_PIPELINE_STAGE_HOST_BIT,0,1,&host,0,nullptr,0,nullptr);
        if(s.profiling) vkCmdWriteTimestamp(h.commands,VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT,h.queries,1);
        check(vkEndCommandBuffer(h.commands));check(vkResetFences(h.device,1,&h.fence));
        VkSubmitInfo info{VK_STRUCTURE_TYPE_SUBMIT_INFO};info.commandBufferCount=1;info.pCommandBuffers=&h.commands;
        check(vkQueueSubmit(h.queue,1,&info,h.fence));check(vkWaitForFences(h.device,1,&h.fence,VK_TRUE,UINT64_MAX));
        if(s.profiling) {
            uint64_t ticks[2];check(vkGetQueryPoolResults(h.device,h.queries,0,2,sizeof(ticks),ticks,sizeof(uint64_t),VK_QUERY_RESULT_64_BIT));
            s.gpuPixelReadMs+=s.ticksMs(ticks[0],ticks[1]);
        }
        if(h.validationErrors) throw std::runtime_error("Resident readback validation errors");
        s.residentDirty=false;s.pixelReadbacks++;s.submissions++;s.readback+=pixels*4;return 0;
    } catch(const std::exception& e) { lastError=e.what();return -1; }
}
int gauntlet_shadow_enqueue(void* context,const uint32_t* texture,const uint32_t* ncc,
    const uint32_t* meta,const uint16_t* color,const uint16_t* depth,int reset) noexcept {
    try {
        if(!context || !texture || !ncc || !meta || !color || !depth) throw std::runtime_error("Null shadow input");
        auto& s=*static_cast<Shadow*>(context);
        if((s.draws.empty())!=(reset!=0)) throw std::runtime_error("Invalid shadow batch reset/order");
        if(s.residentDirty) throw std::runtime_error("Read resident framebuffer before queueing a batch");
        if(s.draws.size()>=128) throw std::runtime_error("Shadow batch draw limit exceeded");
        std::copy_n(meta,256,s.input.begin()+metaAt);
        validateCapture(s.input,true);
        if(reset) {
            s.batch.assign(batchInitial+pixels,0);
            std::copy_n(s.input.begin(),8,s.batch.begin());
            s.batch[4]=128*256;
            std::copy_n(texture,2097152,s.batch.begin()+8);
            std::copy_n(ncc,512,s.batch.begin()+8+2097152);
            for(size_t i=0;i<pixels;i++) s.batch[batchInitial+i]=color[i]|uint32_t(depth[i])<<16;
        }
        std::vector<VkBufferCopy> changes;
        if(!reset) for(size_t p=0;p<2097152+512;p+=256) {
            const uint32_t* now=p<2097152?texture+p:ncc+p-2097152;
            if(std::equal(now,now+256,s.input.begin()+8+p)) continue;
            if((s.batch.size()+256)*4>capacity) throw std::runtime_error("Shadow batch patch capacity exceeded");
            VkBufferCopy copy{s.batch.size()*4,(8+p)*4,1024};
            s.batch.insert(s.batch.end(),now,now+256);
            if(!changes.empty() && changes.back().srcOffset+changes.back().size==copy.srcOffset && changes.back().dstOffset+changes.back().size==copy.dstOffset)
                changes.back().size+=copy.size;
            else changes.push_back(copy);
            s.patchBytes+=1024;
        }
        size_t m=metaAt+s.draws.size()*256;
        std::copy_n(meta,256,s.batch.begin()+m);
        s.draws.push_back({uint32_t(m),0,0,1024});s.updates.push_back(std::move(changes));
        std::copy_n(texture,2097152,s.input.begin()+8);
        std::copy_n(ncc,512,s.input.begin()+8+2097152);
        s.queuedDraws++;return 0;
    } catch(const std::exception& e) { lastError=e.what();return -1; }
}
int gauntlet_shadow_flush(void* context) noexcept {
    try {
        if(!context) throw std::runtime_error("Null shadow context");
        auto& s=*static_cast<Shadow*>(context);
        if(s.draws.empty()) throw std::runtime_error("No queued shadow draws");
        s.submit(s.batch,s.batch.size()*4,batchInitial,s.draws,s.updates,true);
        s.draws.clear();s.updates.clear();return 0;
    } catch(const std::exception& e) { lastError=e.what();return -1; }
}
// No oracle enters the native renderer; C# reads this completed output and
// compares it against the CPU only AFTER the CPU draw has finished.
const uint32_t* gauntlet_shadow_output(void* context) noexcept {
    return static_cast<uint32_t*>(static_cast<Shadow*>(context)->gpu.buffers[3].mapped);
}
}
