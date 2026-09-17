ivec4 rgba(uint p) { return ivec4(p & 255u, (p>>8)&255u, (p>>16)&255u, p>>24); }
ivec4 rgb565(uint p, int a) {
    return ivec4(int((p>>11)&31u)*255/31, int((p>>5)&63u)*255/63, int(p&31u)*255/31, a);
}
uint to565(ivec3 c) { return uint(((c.r>>3)<<11)|((c.g>>2)<<5)|(c.b>>3)); }
int cast32(double v) {
    if (isnan(v) || isinf(v)) return (-2147483647-1);
    if (v >= 2147483647.0lf) return 2147483647;
    if (v <= -2147483648.0lf) return (-2147483647-1);
    return int(v);
}
int64_t i64(uint p) { return int64_t(uint64_t(data[p]) | (uint64_t(data[p+1])<<32)); }
int coord(int v, int size, int lod, bool clamped) {
    int t = v >> (lod+8);
    if (clamped) return clamp(t,0,max(0,size-1));
    if (size<=1) return 0;
    if ((size & (size-1))==0) return t & (size-1);
    t %= size;
    return t<0 ? t+size : t;
}
ivec4 decode(uint raw, uint format, uint lut) {
    int low=int(raw&255u);
    if (format==1u || format==9u) {
        ivec4 c=rgba(data[8u+data[2]+lut+uint(low)]);
        if (format==9u) c.a=int(raw>>8);
        return c;
    }
    if (format==2u) return ivec4(low);
    if (format==3u) return ivec4(low,low,low,255);
    if (format==4u) return ivec4(ivec3((low&15)*17),(low>>4)*17);
    if (format==13u) return ivec4(low,low,low,int(raw>>8));
    if (format==10u) return rgb565(raw,255);
    if (format==0u || format==8u) {
        ivec3 c=ivec3(((low>>5)&7)*255/7,((low>>2)&7)*255/7,(low&3)*255/3);
        return rgb565(to565(c),format==8u ? int(raw>>8) : 255);
    }
    if (format==11u) {
        uint g=(raw>>5)&31u;
        return rgb565((((raw>>10)&31u)<<11)|(((g<<1)|(g>>4))<<5)|(raw&31u), (raw&32768u)!=0u ? 255:0);
    }
    if (format==12u) {
        ivec3 c=ivec3((raw>>8)&15u,(raw>>4)&15u,raw&15u)*17;
        return rgb565(to565(c),int((raw>>12)&15u)*17);
    }
    return ivec4(255,0,255,0); // Host rejects unsupported formats before dispatch.
}
struct SampleState { uint flags,format,baseAddress,bankBase,bankMask,lut; int lod,width,height; };
ivec4 fetch(SampleState v, int x, int y) {
    uint flags=v.flags, format=v.format;
    bool wide=format>=8u;
    uint address=v.bankBase+((v.baseAddress+uint(y*v.width+x)*(wide?2u:1u))&v.bankMask);
    uint word=data[8u+(address>>2)];
    uint raw;
    if (wide) {
        uint lane=(flags&128u)!=0u ? address^2u : address;
        raw=(word>>((lane&2u)*8u))&65535u;
        if ((flags&64u)!=0u) raw=((raw&255u)<<8)|(raw>>8);
    } else {
        uint lane=address&3u;
        if ((flags&256u)!=0u) lane=3u-lane;
        raw=(word>>(lane*8u))&255u;
    }
    return decode(raw,format,v.lut);
}

ivec4 sampleTexture(SampleState v, int64_t s, int64_t t, int64_t w, double reciprocal) {
    uint flags=v.flags; int lod=v.lod,width=v.width,height=v.height;
    int sx,ty;
    if ((flags&1u)!=0u && w!=int64_t(0)) {
        if (reciprocal==0.0lf) reciprocal=256.0lf/double(w);
        sx=cast32(double(s)*reciprocal); ty=cast32(double(t)*reciprocal);
    } else {
        sx=cast32(double(s)*(1.0lf/16777216.0lf)); ty=cast32(double(t)*(1.0lf/16777216.0lf));
    }
    if ((flags&2u)!=0u && w<int64_t(0)) { sx=0;ty=0; }
    bool filtered=(flags&4u)!=0u;
    if (filtered) { sx-=128;ty-=128; }
    int x0=coord(sx,width,lod,(flags&8u)!=0u);
    int y0=coord(ty,height,lod,(flags&16u)!=0u);
    if ((flags&32u)!=0u) y0=height-1-y0;
    ivec4 c=fetch(v,x0,y0);
    if (filtered) {
        int x1=coord(sx+(1<<(lod+8)),width,lod,(flags&8u)!=0u);
        int y1=coord(ty+(1<<(lod+8)),height,lod,(flags&16u)!=0u);
        if ((flags&32u)!=0u) y1=height-1-y1;
        int fx=(sx>>lod)&255,fy=(ty>>lod)&255;
        c=(c*(256-fx)*(256-fy)+fetch(v,x1,y0)*fx*(256-fy)+
           fetch(v,x0,y1)*(256-fx)*fy+fetch(v,x1,y1)*fx*fy+ivec4(32768))>>16;
        c=clamp(c,ivec4(0),ivec4(255));
    }
    return c;
}
