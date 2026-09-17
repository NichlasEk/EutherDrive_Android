#include <vulkan/vulkan.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <cstdlib>
#include <chrono>
#include <cstring>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <stdexcept>
#include <vector>

using Clock=std::chrono::steady_clock;
[[maybe_unused]] static double ms(Clock::time_point start) { return std::chrono::duration<double,std::milli>(Clock::now()-start).count(); }
static void check(VkResult r) { if(r!=VK_SUCCESS) throw std::runtime_error("Vulkan error "+std::to_string(r)); }
static std::vector<uint32_t> load(const char* path) {
    std::ifstream f(path,std::ios::binary|std::ios::ate);
    if(!f) throw std::runtime_error("Cannot open input");
    auto size=f.tellg();
    if(size<=0 || size%4 || size>128*1024*1024) throw std::runtime_error("Invalid input size");
    std::vector<uint32_t> words(size/4); f.seekg(0); f.read(reinterpret_cast<char*>(words.data()),size);
    if(!f) throw std::runtime_error("Truncated input");
    return words;
}
struct Buffer { VkBuffer buffer{}; VkDeviceMemory memory{}; void* mapped{}; };
struct Harness {
    VkInstance instance{}; VkPhysicalDevice physical{}; VkDevice device{}; VkQueue queue{};
    VkCommandPool pool{}; VkCommandBuffer commands{}; VkFence fence{}; VkQueryPool queries{};
    VkDescriptorSetLayout setLayout{}; VkDescriptorPool descriptors{}; VkDescriptorSet set{};
    VkPipelineLayout pipelineLayout{}; VkPipeline pipeline{}; VkShaderModule shader{};
    VkPhysicalDeviceMemoryProperties memoryProperties{}; VkPhysicalDeviceProperties properties{};
    VkDebugUtilsMessengerEXT messenger{};
    std::atomic<uint32_t> validationErrors{0};
    std::array<Buffer,4> buffers{};
    uint32_t family{},timestampBits{};
    ~Harness() {
        if(device) {
            vkDeviceWaitIdle(device);
            for(auto& b:buffers) { if(b.mapped) vkUnmapMemory(device,b.memory); if(b.buffer) vkDestroyBuffer(device,b.buffer,nullptr); if(b.memory) vkFreeMemory(device,b.memory,nullptr); }
            if(pipeline) vkDestroyPipeline(device,pipeline,nullptr);
            if(shader) vkDestroyShaderModule(device,shader,nullptr);
            if(pipelineLayout) vkDestroyPipelineLayout(device,pipelineLayout,nullptr);
            if(descriptors) vkDestroyDescriptorPool(device,descriptors,nullptr);
            if(setLayout) vkDestroyDescriptorSetLayout(device,setLayout,nullptr);
            if(queries) vkDestroyQueryPool(device,queries,nullptr);
            if(fence) vkDestroyFence(device,fence,nullptr);
            if(pool) vkDestroyCommandPool(device,pool,nullptr);
            vkDestroyDevice(device,nullptr);
        }
        if(messenger) reinterpret_cast<PFN_vkDestroyDebugUtilsMessengerEXT>(vkGetInstanceProcAddr(instance,"vkDestroyDebugUtilsMessengerEXT"))(instance,messenger,nullptr);
        if(instance) vkDestroyInstance(instance,nullptr);
    }
    static VKAPI_ATTR VkBool32 VKAPI_CALL message(VkDebugUtilsMessageSeverityFlagBitsEXT severity,
        VkDebugUtilsMessageTypeFlagsEXT, const VkDebugUtilsMessengerCallbackDataEXT* data, void* user) {
        if(severity&VK_DEBUG_UTILS_MESSAGE_SEVERITY_ERROR_BIT_EXT) static_cast<Harness*>(user)->validationErrors++;
        std::cerr<<"validation: "<<data->pMessage<<"\n";return VK_FALSE;
    }
    void buffer(unsigned index, VkDeviceSize size, VkBufferUsageFlags usage, VkMemoryPropertyFlags flags,
        VkMemoryPropertyFlags preferred=0) {
        auto& b=buffers[index];
        VkBufferCreateInfo ci{VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO}; ci.size=size;ci.usage=usage;ci.sharingMode=VK_SHARING_MODE_EXCLUSIVE;
        check(vkCreateBuffer(device,&ci,nullptr,&b.buffer));
        VkMemoryRequirements req;vkGetBufferMemoryRequirements(device,b.buffer,&req);
        uint32_t type=UINT32_MAX;
        for(uint32_t i=0;i<memoryProperties.memoryTypeCount;i++)
            if((req.memoryTypeBits&(1u<<i)) && (memoryProperties.memoryTypes[i].propertyFlags&flags)==flags) {
                if(type==UINT32_MAX) type=i;
                if((memoryProperties.memoryTypes[i].propertyFlags&preferred)==preferred) { type=i;break; }
            }
        if(type==UINT32_MAX) throw std::runtime_error("Required memory type unavailable");
        VkMemoryAllocateInfo ai{VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO};ai.allocationSize=req.size;ai.memoryTypeIndex=type;
        check(vkAllocateMemory(device,&ai,nullptr,&b.memory));check(vkBindBufferMemory(device,b.buffer,b.memory,0));
        if(flags&VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT) check(vkMapMemory(device,b.memory,0,size,0,&b.mapped));
    }
    void init(const std::vector<uint32_t>& spirv, size_t inputBytes, size_t outputBytes) {
        VkApplicationInfo app{VK_STRUCTURE_TYPE_APPLICATION_INFO};app.pApplicationName="Gauntlet isolated sampler";app.apiVersion=VK_API_VERSION_1_1;
        VkInstanceCreateInfo ic{VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO};ic.pApplicationInfo=&app;
        const char* layer="VK_LAYER_KHRONOS_validation";
        const char* extensions[]={VK_EXT_DEBUG_UTILS_EXTENSION_NAME,VK_EXT_VALIDATION_FEATURES_EXTENSION_NAME};
        VkValidationFeatureEnableEXT feature=VK_VALIDATION_FEATURE_ENABLE_SYNCHRONIZATION_VALIDATION_EXT;
        VkValidationFeaturesEXT validation{VK_STRUCTURE_TYPE_VALIDATION_FEATURES_EXT};
        validation.enabledValidationFeatureCount=1;validation.pEnabledValidationFeatures=&feature;
        VkDebugUtilsMessengerCreateInfoEXT debug{VK_STRUCTURE_TYPE_DEBUG_UTILS_MESSENGER_CREATE_INFO_EXT};
        debug.messageSeverity=VK_DEBUG_UTILS_MESSAGE_SEVERITY_ERROR_BIT_EXT|VK_DEBUG_UTILS_MESSAGE_SEVERITY_WARNING_BIT_EXT;
        debug.messageType=VK_DEBUG_UTILS_MESSAGE_TYPE_VALIDATION_BIT_EXT|VK_DEBUG_UTILS_MESSAGE_TYPE_GENERAL_BIT_EXT|VK_DEBUG_UTILS_MESSAGE_TYPE_PERFORMANCE_BIT_EXT;
        debug.pfnUserCallback=message;debug.pUserData=this;
        if(std::getenv("GAUNTLET_GPU_VALIDATION")) {
            ic.enabledLayerCount=1;ic.ppEnabledLayerNames=&layer;ic.enabledExtensionCount=2;ic.ppEnabledExtensionNames=extensions;
            validation.pNext=&debug;ic.pNext=&validation;
        }
        check(vkCreateInstance(&ic,nullptr,&instance));
        if(ic.pNext) check(reinterpret_cast<PFN_vkCreateDebugUtilsMessengerEXT>(vkGetInstanceProcAddr(instance,"vkCreateDebugUtilsMessengerEXT"))(instance,&debug,nullptr,&messenger));
        uint32_t count=0;check(vkEnumeratePhysicalDevices(instance,&count,nullptr));
        std::vector<VkPhysicalDevice> devices(count);check(vkEnumeratePhysicalDevices(instance,&count,devices.data()));
        for(auto candidate:devices) {
            VkPhysicalDeviceProperties p;vkGetPhysicalDeviceProperties(candidate,&p);
            if(p.deviceType==VK_PHYSICAL_DEVICE_TYPE_DISCRETE_GPU) { physical=candidate;properties=p;break; }
        }
        if(!physical) throw std::runtime_error("No discrete GPU; refusing a software fallback benchmark");
        VkPhysicalDeviceFeatures supported;vkGetPhysicalDeviceFeatures(physical,&supported);
        if(!supported.shaderFloat64 || !supported.shaderInt64) throw std::runtime_error("Exact sampler requires shaderFloat64 and shaderInt64");
        if(inputBytes>properties.limits.maxStorageBufferRange || outputBytes>properties.limits.maxStorageBufferRange)
            throw std::runtime_error("Storage buffer limit exceeded");
        vkGetPhysicalDeviceQueueFamilyProperties(physical,&count,nullptr);
        std::vector<VkQueueFamilyProperties> families(count);vkGetPhysicalDeviceQueueFamilyProperties(physical,&count,families.data());
        family=UINT32_MAX;
        for(uint32_t i=0;i<count;i++) if((families[i].queueFlags&VK_QUEUE_COMPUTE_BIT) && families[i].timestampValidBits) {
            family=i;timestampBits=families[i].timestampValidBits;break;
        }
        if(family==UINT32_MAX) throw std::runtime_error("No compute queue with timestamps");
        float priority=1;
        VkDeviceQueueCreateInfo qc{VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO};qc.queueFamilyIndex=family;qc.queueCount=1;qc.pQueuePriorities=&priority;
        VkPhysicalDeviceFeatures enabled{};enabled.shaderInt64=VK_TRUE;enabled.shaderFloat64=VK_TRUE;
        VkDeviceCreateInfo dc{VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO};dc.queueCreateInfoCount=1;dc.pQueueCreateInfos=&qc;dc.pEnabledFeatures=&enabled;
        check(vkCreateDevice(physical,&dc,nullptr,&device));vkGetDeviceQueue(device,family,0,&queue);vkGetPhysicalDeviceMemoryProperties(physical,&memoryProperties);
        buffer(0,inputBytes,VK_BUFFER_USAGE_TRANSFER_SRC_BIT,VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT|VK_MEMORY_PROPERTY_HOST_COHERENT_BIT);
        buffer(1,inputBytes,VK_BUFFER_USAGE_TRANSFER_SRC_BIT|VK_BUFFER_USAGE_TRANSFER_DST_BIT|VK_BUFFER_USAGE_STORAGE_BUFFER_BIT,VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT);
        buffer(2,outputBytes,VK_BUFFER_USAGE_TRANSFER_DST_BIT|VK_BUFFER_USAGE_TRANSFER_SRC_BIT|VK_BUFFER_USAGE_STORAGE_BUFFER_BIT,VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT);
        buffer(3,outputBytes,VK_BUFFER_USAGE_TRANSFER_DST_BIT,VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT|VK_MEMORY_PROPERTY_HOST_COHERENT_BIT,VK_MEMORY_PROPERTY_HOST_CACHED_BIT);
        VkCommandPoolCreateInfo pc{VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO};pc.queueFamilyIndex=family;pc.flags=VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT;
        check(vkCreateCommandPool(device,&pc,nullptr,&pool));
        VkCommandBufferAllocateInfo ca{VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO};ca.commandPool=pool;ca.level=VK_COMMAND_BUFFER_LEVEL_PRIMARY;ca.commandBufferCount=1;
        check(vkAllocateCommandBuffers(device,&ca,&commands));
        VkFenceCreateInfo fc{VK_STRUCTURE_TYPE_FENCE_CREATE_INFO};check(vkCreateFence(device,&fc,nullptr,&fence));
        VkQueryPoolCreateInfo qp{VK_STRUCTURE_TYPE_QUERY_POOL_CREATE_INFO};qp.queryType=VK_QUERY_TYPE_TIMESTAMP;qp.queryCount=4;
        check(vkCreateQueryPool(device,&qp,nullptr,&queries));
        std::array<VkDescriptorSetLayoutBinding,2> bindings{};
        for(uint32_t i=0;i<2;i++) { bindings[i].binding=i;bindings[i].descriptorType=VK_DESCRIPTOR_TYPE_STORAGE_BUFFER;bindings[i].descriptorCount=1;bindings[i].stageFlags=VK_SHADER_STAGE_COMPUTE_BIT; }
        VkDescriptorSetLayoutCreateInfo sl{VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO};sl.bindingCount=2;sl.pBindings=bindings.data();
        check(vkCreateDescriptorSetLayout(device,&sl,nullptr,&setLayout));
        VkDescriptorPoolSize ps{VK_DESCRIPTOR_TYPE_STORAGE_BUFFER,2};
        VkDescriptorPoolCreateInfo dp{VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO};dp.maxSets=1;dp.poolSizeCount=1;dp.pPoolSizes=&ps;
        check(vkCreateDescriptorPool(device,&dp,nullptr,&descriptors));
        VkDescriptorSetAllocateInfo sa{VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO};sa.descriptorPool=descriptors;sa.descriptorSetCount=1;sa.pSetLayouts=&setLayout;
        check(vkAllocateDescriptorSets(device,&sa,&set));
        std::array<VkDescriptorBufferInfo,2> bi{{{buffers[1].buffer,0,inputBytes},{buffers[2].buffer,0,outputBytes}}};
        std::array<VkWriteDescriptorSet,2> writes{};
        for(uint32_t i=0;i<2;i++) { writes[i].sType=VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET;writes[i].dstSet=set;writes[i].dstBinding=i;writes[i].descriptorCount=1;writes[i].descriptorType=VK_DESCRIPTOR_TYPE_STORAGE_BUFFER;writes[i].pBufferInfo=&bi[i]; }
        vkUpdateDescriptorSets(device,2,writes.data(),0,nullptr);
        VkPipelineLayoutCreateInfo pl{VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO};pl.setLayoutCount=1;pl.pSetLayouts=&setLayout;
        VkPushConstantRange push{VK_SHADER_STAGE_COMPUTE_BIT,0,16};pl.pushConstantRangeCount=1;pl.pPushConstantRanges=&push;
        check(vkCreatePipelineLayout(device,&pl,nullptr,&pipelineLayout));
        VkShaderModuleCreateInfo sm{VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO};sm.codeSize=spirv.size()*4;sm.pCode=spirv.data();
        check(vkCreateShaderModule(device,&sm,nullptr,&shader));
        VkComputePipelineCreateInfo cp{VK_STRUCTURE_TYPE_COMPUTE_PIPELINE_CREATE_INFO};cp.layout=pipelineLayout;
        cp.stage.sType=VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO;cp.stage.stage=VK_SHADER_STAGE_COMPUTE_BIT;cp.stage.module=shader;cp.stage.pName="main";
        check(vkCreateComputePipelines(device,VK_NULL_HANDLE,1,&cp,nullptr,&pipeline));
    }
    void record(size_t inputBytes,size_t outputBytes,uint32_t count,int upload,size_t tail,
        const std::vector<std::array<uint32_t,4>>& draws,size_t initial,
        const std::vector<std::vector<VkBufferCopy>>& updates,bool resetOutput=true,size_t statisticsBytes=0,bool statisticsOnly=false,
        const std::vector<VkBufferCopy>* sparseUploads=nullptr,bool profileTransfers=false) {
        check(vkResetCommandBuffer(commands,0));
        VkCommandBufferBeginInfo begin{VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO};check(vkBeginCommandBuffer(commands,&begin));
        vkCmdResetQueryPool(commands,queries,0,profileTransfers?4:2);
        if(profileTransfers) vkCmdWriteTimestamp(commands,VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT,queries,2);
        VkMemoryBarrier reuse{VK_STRUCTURE_TYPE_MEMORY_BARRIER};reuse.srcAccessMask=VK_ACCESS_MEMORY_READ_BIT|VK_ACCESS_MEMORY_WRITE_BIT;
        reuse.dstAccessMask=VK_ACCESS_TRANSFER_READ_BIT|VK_ACCESS_TRANSFER_WRITE_BIT|VK_ACCESS_SHADER_READ_BIT|VK_ACCESS_SHADER_WRITE_BIT;
        vkCmdPipelineBarrier(commands,VK_PIPELINE_STAGE_ALL_COMMANDS_BIT,VK_PIPELINE_STAGE_TRANSFER_BIT|VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT,0,1,&reuse,0,nullptr,0,nullptr);
        if(upload) {
            if(sparseUploads) {
                vkCmdCopyBuffer(commands,buffers[0].buffer,buffers[1].buffer,uint32_t(sparseUploads->size()),sparseUploads->data());
            } else if(upload==2) {
                VkBufferCopy copy{0,0,inputBytes};vkCmdCopyBuffer(commands,buffers[0].buffer,buffers[1].buffer,1,&copy);
            } else {
                VkBufferCopy copies[]={{0,0,32},{tail,tail,inputBytes-tail}};
                vkCmdCopyBuffer(commands,buffers[0].buffer,buffers[1].buffer,2,copies);
            }
            VkMemoryBarrier ready{VK_STRUCTURE_TYPE_MEMORY_BARRIER};ready.srcAccessMask=VK_ACCESS_TRANSFER_WRITE_BIT;ready.dstAccessMask=VK_ACCESS_SHADER_READ_BIT|VK_ACCESS_TRANSFER_READ_BIT;
            vkCmdPipelineBarrier(commands,VK_PIPELINE_STAGE_TRANSFER_BIT,VK_PIPELINE_STAGE_TRANSFER_BIT|VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT,0,1,&ready,0,nullptr,0,nullptr);
        }
        if(!draws.empty() && resetOutput) {
            VkBufferCopy init{initial*4,0,outputBytes-statisticsBytes};vkCmdCopyBuffer(commands,buffers[1].buffer,buffers[2].buffer,1,&init);
            VkMemoryBarrier ready{VK_STRUCTURE_TYPE_MEMORY_BARRIER};ready.srcAccessMask=VK_ACCESS_TRANSFER_WRITE_BIT;ready.dstAccessMask=VK_ACCESS_SHADER_READ_BIT|VK_ACCESS_SHADER_WRITE_BIT;
            vkCmdPipelineBarrier(commands,VK_PIPELINE_STAGE_TRANSFER_BIT,VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT,0,1,&ready,0,nullptr,0,nullptr);
        }
        if(statisticsBytes) {
            vkCmdFillBuffer(commands,buffers[2].buffer,outputBytes-statisticsBytes,statisticsBytes,0);
            VkMemoryBarrier ready{VK_STRUCTURE_TYPE_MEMORY_BARRIER};ready.srcAccessMask=VK_ACCESS_TRANSFER_WRITE_BIT;ready.dstAccessMask=VK_ACCESS_SHADER_READ_BIT|VK_ACCESS_SHADER_WRITE_BIT;
            vkCmdPipelineBarrier(commands,VK_PIPELINE_STAGE_TRANSFER_BIT,VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT,0,1,&ready,0,nullptr,0,nullptr);
        }
        vkCmdBindPipeline(commands,VK_PIPELINE_BIND_POINT_COMPUTE,pipeline);
        vkCmdBindDescriptorSets(commands,VK_PIPELINE_BIND_POINT_COMPUTE,pipelineLayout,0,1,&set,0,nullptr);
        vkCmdWriteTimestamp(commands,VK_PIPELINE_STAGE_ALL_COMMANDS_BIT,queries,0);
        if(draws.empty()) vkCmdDispatch(commands,(count+127)/128,1,1);
        for(size_t d=0;d<draws.size();d++) {
            if(d) {
                VkMemoryBarrier ordered{VK_STRUCTURE_TYPE_MEMORY_BARRIER};ordered.srcAccessMask=VK_ACCESS_SHADER_WRITE_BIT;ordered.dstAccessMask=VK_ACCESS_SHADER_READ_BIT|VK_ACCESS_SHADER_WRITE_BIT;
                vkCmdPipelineBarrier(commands,VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT,VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT,0,1,&ordered,0,nullptr,0,nullptr);
            }
            if(!updates[d].empty() && !(d && std::getenv("GAUNTLET_GPU_DROP_TEXTURE_UPDATES"))) {
                VkMemoryBarrier writable{VK_STRUCTURE_TYPE_MEMORY_BARRIER};
                writable.srcAccessMask=VK_ACCESS_SHADER_READ_BIT|VK_ACCESS_TRANSFER_WRITE_BIT;
                writable.dstAccessMask=VK_ACCESS_TRANSFER_READ_BIT|VK_ACCESS_TRANSFER_WRITE_BIT;
                vkCmdPipelineBarrier(commands,VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT|VK_PIPELINE_STAGE_TRANSFER_BIT,VK_PIPELINE_STAGE_TRANSFER_BIT,0,1,&writable,0,nullptr,0,nullptr);
                vkCmdCopyBuffer(commands,buffers[1].buffer,buffers[1].buffer,uint32_t(updates[d].size()),updates[d].data());
                VkMemoryBarrier sampled{VK_STRUCTURE_TYPE_MEMORY_BARRIER};sampled.srcAccessMask=VK_ACCESS_TRANSFER_WRITE_BIT;sampled.dstAccessMask=VK_ACCESS_SHADER_READ_BIT;
                vkCmdPipelineBarrier(commands,VK_PIPELINE_STAGE_TRANSFER_BIT,VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT,0,1,&sampled,0,nullptr,0,nullptr);
            }
            vkCmdPushConstants(commands,pipelineLayout,VK_SHADER_STAGE_COMPUTE_BIT,0,16,draws[d].data());
            vkCmdDispatch(commands,(count+127)/128,1,1);
        }
        vkCmdWriteTimestamp(commands,VK_PIPELINE_STAGE_ALL_COMMANDS_BIT,queries,1);
        VkMemoryBarrier done{VK_STRUCTURE_TYPE_MEMORY_BARRIER};done.srcAccessMask=VK_ACCESS_SHADER_WRITE_BIT|VK_ACCESS_TRANSFER_WRITE_BIT;done.dstAccessMask=VK_ACCESS_TRANSFER_READ_BIT;
        vkCmdPipelineBarrier(commands,VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT|VK_PIPELINE_STAGE_TRANSFER_BIT,VK_PIPELINE_STAGE_TRANSFER_BIT,0,1,&done,0,nullptr,0,nullptr);
        VkDeviceSize start=statisticsOnly?outputBytes-statisticsBytes:0;
        VkBufferCopy copy{start,start,outputBytes-start};vkCmdCopyBuffer(commands,buffers[2].buffer,buffers[3].buffer,1,&copy);
        VkMemoryBarrier host{VK_STRUCTURE_TYPE_MEMORY_BARRIER};host.srcAccessMask=VK_ACCESS_TRANSFER_WRITE_BIT;host.dstAccessMask=VK_ACCESS_HOST_READ_BIT;
        vkCmdPipelineBarrier(commands,VK_PIPELINE_STAGE_TRANSFER_BIT,VK_PIPELINE_STAGE_HOST_BIT,0,1,&host,0,nullptr,0,nullptr);
        if(profileTransfers) vkCmdWriteTimestamp(commands,VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT,queries,3);
        check(vkEndCommandBuffer(commands));
    }
};
[[maybe_unused]] static double median(std::vector<double> v) { std::sort(v.begin(),v.end());return v[v.size()/2]; }
static bool validateCapture(const std::vector<uint32_t>& input,bool allowStatistics=false) {
    bool stream=input.size()>=8 && input[0]==0x32524447;
    bool draw=stream || (input.size()>=8 && input[0]==0x31524447);
    if(input.size()<8 || (!draw && input[0]!=0x31435347) || input[1]!=(stream?2u:1u) || input[2]!=2097152 || input[3]!=512 ||
        input[4]!=(draw?256u:20u) || input[5]==0 || input[5]>(stream?2097152u:1048576u) ||
        input.size()!=8ull+input[2]+input[3]+(draw?256ull+2ull*input[5]:20ull*input[5])) throw std::runtime_error("Invalid GSC1/GDR1 capture");
    uint32_t count=input[5];size_t requests=8+input[2]+input[3];
    if(draw) {
        auto m=&input[requests];
        if(m[31]>(allowStatistics?1u:0u)) throw std::runtime_error("Unsupported capture statistics flag");
        if(m[28]>1u) throw std::runtime_error("Unsupported draw color path");
        if(!m[2] || !m[3] || m[2]>1024 || m[3]>2048 || uint64_t(m[0])+m[2]>1024 || uint64_t(m[1])+m[3]>2048 || (stream?count!=2097152:uint64_t(m[2])*m[3]!=count) || m[19]!=0xb4779 || m[23]!=(stream?1u:0u))
            throw std::runtime_error("Unsupported draw geometry/state");
        if(stream && m[30]!=UINT32_MAX && (m[30]>=2048 || uint64_t(m[1])+m[3]>m[30]+1))
            throw std::runtime_error("Unsupported aliased raster origin");
        for(uint32_t t=0;t<2;t++) {
            auto s=m+128+64*t;
            if(!s[13]) continue;
            if(!(s[3]<=4 || (s[3]>=8&&s[3]<=13)) || (s[4]&~511u) || s[7]!=256*t || s[16]>8 ||
                !((s[6]==4194303&&(s[5]==0||s[5]==4194304))||(s[6]==8388607&&s[5]==0)))
                throw std::runtime_error("Unsupported draw texture state");
            for(int l=0;l<9;l++) if(!s[24+3*l]||!s[25+3*l]||s[24+3*l]>256||s[25+3*l]>256)
                throw std::runtime_error("Invalid draw texture layout");
        }
    }
    for(uint32_t i=0;!draw && i<count;i++) {
        const auto* r=&input[requests+i*20];
        bool format=r[13]<=4 || (r[13]>=8 && r[13]<=13);
        if(r[9]>8 || !r[10] || !r[11] || r[10]>256 || r[11]>256 || !format || (r[8]&~511u) ||
            (r[14]!=0 && r[14]!=256) ||
            !((r[17]==4194303 && (r[16]==0 || r[16]==4194304)) || (r[17]==8388607 && r[16]==0)))
            throw std::runtime_error("Unsupported or malformed sampler request");
    }
    return draw;
}
#ifndef GAUNTLET_GPU_LIBRARY
int main(int argc,char** argv) try {
    if(argc<3 || argc>66) throw std::runtime_error("usage: gauntlet-gpu-probe capture.gsc sample.spv | first.gdr draw.spv [next.gdr ...]");
    auto input=load(argv[1]),spirv=load(argv[2]);
    bool draw=validateCapture(input);
    bool stream=input[0]==0x32524447;
    if(!draw && argc!=3) throw std::runtime_error("Only draw captures support batching");
    size_t requests=8+input[2]+input[3];
    std::vector<std::array<uint32_t,4>> draws;
    std::vector<std::vector<VkBufferCopy>> updates;
    size_t updateBytes=0,updateRegions=0,textureUpdateBytes=0,nccUpdateBytes=0;
    bool relocation=std::getenv("GAUNTLET_GPU_TEST_TEXTURE_RELOCATION")!=nullptr;
    if(relocation && (!stream || argc<4)) throw std::runtime_error("Texture relocation test requires at least two GDR2 draws");
    if(draw) {
        std::vector<std::vector<uint32_t>> captures;captures.push_back(std::move(input));
        for(int a=3;a<argc;a++) {
            auto c=load(argv[a]);
            if(!validateCapture(c)) throw std::runtime_error("Expected draw capture");
            if(c[0]!=captures[0][0] || c[7]!=captures[0][7] || (!stream && !std::equal(c.begin()+8,c.begin()+requests,captures[0].begin()+8)))
                throw std::runtime_error("Incompatible batch: texture/NCC state or draw buffer changed");
            if(relocation && (captures.size()&1)) {
                // Metamorphic test, not a guest capture: rotate each texture
                // bank by 1024 bytes and relocate every LOD base equally.
                // All sampled bytes (and thus the CPU oracle) must be unchanged.
                uint32_t bankMask=0;
                for(size_t t=0;t<2;t++) {
                    auto s=&c[requests+128+64*t];
                    if(!s[13]) continue;
                    if(bankMask && bankMask!=s[6]) throw std::runtime_error("Relocation requires uniform texture banks");
                    bankMask=s[6];
                    for(int l=0;l<9;l++) s[26+3*l]=(s[26+3*l]+1024)&bankMask;
                }
                if(!bankMask) throw std::runtime_error("Relocation requires a texture state");
                size_t bankWords=(bankMask+1)/4;
                for(size_t p=8;p<8+c[2];p+=bankWords)
                    std::rotate(c.begin()+p,c.begin()+p+bankWords-256,c.begin()+p+bankWords);
            }
            captures.push_back(std::move(c));
        }
        uint32_t left=1024,top=2048,right=0,bottom=0;
        for(const auto& c:captures) {
            auto m=&c[requests];left=std::min(left,m[0]);top=std::min(top,m[1]);
            right=std::max(right,m[0]+m[2]);bottom=std::max(bottom,m[1]+m[3]);
        }
        if(stream) { left=top=0;right=1024;bottom=2048; }
        uint32_t width=right-left,count=width*(bottom-top);
        if(count>(stream?2097152u:1048576u)) throw std::runtime_error("Batch rectangle too large");
        std::vector<uint32_t> initial(count),expected(count);std::vector<bool> known(count);
        input.assign(captures[0].begin(),captures[0].begin()+requests);
        input[4]=256*captures.size();input[5]=count;
        for(const auto& c:captures) {
            auto m=&c[requests];draws.push_back({uint32_t(input.size()),left,top,width});
            input.insert(input.end(),m,m+256);
            for(uint32_t j=0;j<c[5];j++) {
                size_t i=stream?j:(m[1]-top+j/m[2])*width+m[0]-left+j%m[2];
                uint32_t before=c[requests+256+j];
                if(known[i] && expected[i]!=before) throw std::runtime_error("Incompatible batch: framebuffer pre/post chain mismatch");
                if(!known[i]) { initial[i]=before;known[i]=true; }
                expected[i]=c[requests+256+c[5]+j];
            }
        }
        input.insert(input.end(),initial.begin(),initial.end());
        updates.resize(captures.size());
        if(stream) {
            // Diff input-only 1 KiB pages. d=0 restores every page changed by
            // any later draw, so resident repetitions always start identically.
            constexpr size_t page=256;
            std::vector<bool> touched((requests-8)/page);
            auto appendPage=[&](size_t d,size_t p,const std::vector<uint32_t>& c) {
                VkBufferCopy copy{input.size()*4,(8+p*page)*4,page*4};
                input.insert(input.end(),c.begin()+8+p*page,c.begin()+8+(p+1)*page);
                if(!updates[d].empty() && updates[d].back().srcOffset+updates[d].back().size==copy.srcOffset && updates[d].back().dstOffset+updates[d].back().size==copy.dstOffset)
                    updates[d].back().size+=copy.size;
                else updates[d].push_back(copy);
                updateBytes+=copy.size;
                if(8+p*page<8+input[2]) textureUpdateBytes+=copy.size;
                else nccUpdateBytes+=copy.size;
            };
            for(size_t d=1;d<captures.size();d++) for(size_t p=0;p<touched.size();p++) {
                auto at=8+p*page;
                if(!std::equal(captures[d].begin()+at,captures[d].begin()+at+page,captures[d-1].begin()+at)) {
                    touched[p]=true;appendPage(d,p,captures[d]);
                }
            }
            for(size_t p=0;p<touched.size();p++) if(touched[p]) appendPage(0,p,captures[0]);
            for(const auto& u:updates) updateRegions+=u.size();
        }
        input.insert(input.end(),expected.begin(),expected.end());
    }
    uint32_t count=input[5];
    // Draw oracle stays exclusively on the host; sampler oracle is never read by its shader.
    size_t initial=requests+input[4],oracle=draw?input.size()-count:requests+15;
    size_t inputBytes=(draw?oracle:input.size())*4;
    if(std::getenv("GAUNTLET_GPU_CORRUPT_ORACLE")) input[oracle]^=1u;
    auto setup=Clock::now(); Harness h; h.init(spirv,inputBytes,count*4);
    std::cout<<"gpu="<<h.properties.deviceName<<" samples="<<count<<" draws="<<draws.size()<<" setupMs="<<ms(setup)<<"\n";
    if(stream) std::cout<<"stream=full-frame-chain updateBytes="<<updateBytes<<" textureUpdateBytes="<<textureUpdateBytes<<" nccUpdateBytes="<<nccUpdateBytes<<" updateRegions="<<updateRegions<<" relocationTest="<<relocation<<" (includes repetition reset)\n";
    if((count+127)/128>h.properties.limits.maxComputeWorkGroupCount[0]) throw std::runtime_error("Dispatch limit exceeded");
    std::vector<uint32_t> output(count);
    // Upload mode first initializes device memory. Every timed run validates
    // EVERY RGBA channel, after stopping the host timer.
    for(int upload:{2,1,0}) {
        size_t tail=(8+input[2])*4;
        h.record(inputBytes,count*4,count,upload,tail,draws,initial,updates);
        std::vector<double> wall,gpu;
        for(int rep=0;rep<12;rep++) {
            auto start=Clock::now();
            if(upload==2) std::memcpy(h.buffers[0].mapped,input.data(),inputBytes);
            if(upload==1) {
                std::memcpy(h.buffers[0].mapped,input.data(),32);
                std::memcpy(static_cast<char*>(h.buffers[0].mapped)+tail,reinterpret_cast<char*>(input.data())+tail,inputBytes-tail);
            }
            check(vkResetFences(h.device,1,&h.fence));
            VkSubmitInfo submit{VK_STRUCTURE_TYPE_SUBMIT_INFO};submit.commandBufferCount=1;submit.pCommandBuffers=&h.commands;
            check(vkQueueSubmit(h.queue,1,&submit,h.fence));check(vkWaitForFences(h.device,1,&h.fence,VK_TRUE,UINT64_MAX));
            std::memcpy(output.data(),h.buffers[3].mapped,count*4);
            double elapsed=ms(start);
            uint64_t ticks[2];check(vkGetQueryPoolResults(h.device,h.queries,0,2,sizeof(ticks),ticks,sizeof(uint64_t),VK_QUERY_RESULT_64_BIT|VK_QUERY_RESULT_WAIT_BIT));
            uint64_t mask=h.timestampBits==64 ? UINT64_MAX : (uint64_t(1)<<h.timestampBits)-1;
            size_t mismatches=0;
            for(uint32_t i=0;i<count;i++) if(output[i]!=input[oracle+i*(draw?1:20)]) {
                if(mismatches<8) std::cerr<<"mismatch i="<<i<<" expected="<<std::hex<<input[oracle+i*(draw?1:20)]<<" actual="<<output[i]<<std::dec<<"\n";
                mismatches++;
            }
            if(mismatches) throw std::runtime_error(std::to_string(mismatches)+(draw?" color/depth mismatches":" RGBA mismatches"));
            if(rep>=3) { wall.push_back(elapsed);gpu.push_back(((ticks[1]-ticks[0])&mask)*h.properties.limits.timestampPeriod/1e6); }
        }
        std::cout<<std::fixed<<std::setprecision(4)<<"mode="<<(upload==2?"full-upload":upload==1?(draw?"draw-upload":"requests-upload"):"resident")<<" hostMedianMs="<<median(wall)
            <<" gpuMedianMs="<<median(gpu)<<" samples="<<count<<(draw?" colorDepthMismatch=0":" rgbaMismatch=0")<<" oracle=PASS\n";
    }
    if(h.validationErrors) throw std::runtime_error("Vulkan validation reported errors");
    if(h.messenger) std::cout<<"synchronizationValidationErrors=0\n";
    return 0;
} catch(const std::exception& e) { std::cerr<<e.what()<<"\n";return 1; }
#endif
