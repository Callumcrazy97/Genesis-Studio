namespace Genesis.Rendering.Particles;

/// <summary>One HLSL simulation/render implementation, compiled to DXBC, DXIL, SPIR-V or GLSL.</summary>
public static class GpuParticleShaders
{
    public static readonly string[] ComputeEntries = ["Reset", "BeginStep", "CollectEvents", "UpdateScan", "PrefixGroups", "SpawnCompact", "FinishStep", "DrawArguments"];

    private const string Common = """
#define THREADS 128
#define TRAIL_SAMPLES 8
#define GROUP_BASE 64
#define LOCAL_BASE 8256
#define EVENT_CAPACITY 4096
#define CURVE_SAMPLES 128
struct Particle {
    float4 positionAge;
    float4 velocityLife;
    float4 rotationSpeedScale;
    float4 identity;
    float4 previousEvent;
    float4 trail[TRAIL_SAMPLES];
};
struct ParticleEvent { float4 positionMask; float4 velocity; };
cbuffer Parameters : register(b1) {
    row_major float4x4 World;
    row_major float4x4 InverseWorld;
    float4 Emission;
    float4 Box;
    float4 Motion;
    float4 Forces;
    float4 Wind;
    float4 Sizes;
    float4 Rotation;
    float4 Collision;
    float4 Render;
    float4 Flipbook;
    float4 Options;
    float4 Beam;
    float4 Geometry;
};
uint Hash(uint x) {
    x ^= x >> 16; x *= 0x7feb352d; x ^= x >> 15; x *= 0x846ca68b; x ^= x >> 16; return x;
}
float Random01(inout uint seed) { seed = Hash(seed + 0x9e3779b9); return (seed >> 8) * (1.0 / 16777216.0); }
float Sym(inout uint seed) { return Random01(seed) * 2.0 - 1.0; }
float3 SafeNormal(float3 v, float3 fallback) { float len = dot(v,v); return len > 0.0000001 ? v * rsqrt(len) : fallback; }
float3 ToWorld(float3 p) { return Options.x > 0.5 ? mul(float4(p,1), World).xyz : p; }
float3 FromWorld(float3 p) { return Options.x > 0.5 ? mul(float4(p,1), InverseWorld).xyz : p; }
float3 DirectionWorld(float3 v) { return Options.x > 0.5 ? mul(float4(v,0),World).xyz : v; }
float3 DirectionLocal(float3 v) { return Options.x > 0.5 ? mul(float4(v,0),InverseWorld).xyz : v; }
""";

    public static readonly string Compute = Common + """
RWStructuredBuffer<Particle> State : register(u0);
RWStructuredBuffer<uint> Pool : register(u1);
RWStructuredBuffer<ParticleEvent> Events : register(u2);
RWByteAddressBuffer Arguments : register(u3);
StructuredBuffer<float4> Lookup : register(t0);
StructuredBuffer<ParticleEvent> ParentEvents : register(t1);
StructuredBuffer<uint> ParentPool : register(t2);
cbuffer Step : register(b0) {
    float4 Timing;
    uint Capacity, Groups, Seed, Sequence;
    float4 EventSettings;
    float4 Limits;
};
groupshared uint3 Scan[THREADS];
void SetArgument(uint index, uint value) { Arguments.Store(index * 4, value); }
uint AliveBase() { return LOCAL_BASE + Capacity * 4; }
float4 Curve(float t, uint offset) {
    float x = saturate(t) * (CURVE_SAMPLES - 1);
    uint a = (uint)x;
    return lerp(Lookup[offset+a], Lookup[offset+min(a+1,CURVE_SAMPLES-1)], frac(x));
}

[numthreads(THREADS,1,1)]
void Reset(uint3 tid : SV_DispatchThreadID) {
    uint i = tid.x;
    if (i < Capacity) State[i] = (Particle)0;
    if (i < 64) Pool[i] = 0;
    if (i < 16) SetArgument(i, 0);
    if (i == 0) { Pool[13] = 0xffffffff; Pool[14] = 0; }
}

[numthreads(1,1,1)]
void BeginStep(uint3 tid : SV_DispatchThreadID) {
    Pool[8] = 0; Pool[9] = 0; Pool[10] = 0;
    Pool[11] = (uint)max(0, Timing.y);
}

[numthreads(THREADS,1,1)]
void CollectEvents(uint3 tid : SV_DispatchThreadID) {
    uint i = tid.x;
    if (i >= min(ParentPool[8], EVENT_CAPACITY)) return;
    ParticleEvent ev = ParentEvents[i];
    if (((uint)ev.positionMask.w & (uint)EventSettings.x) == 0) return;
    uint rng = Seed ^ Hash(Sequence + i * 17 + (uint)EventSettings.x);
    if (Random01(rng) >= EventSettings.y) return;
    uint repeat = min((uint)EventSettings.z,32);
    for (uint n=0;n<repeat;n++) {
        uint at; InterlockedAdd(Pool[10],1,at);
        if (at >= EVENT_CAPACITY) { InterlockedAdd(Pool[7],1); continue; }
        ev.velocity.xyz *= EventSettings.w;
        // The incoming queue occupies the second half; output events use the first half.
        Events[EVENT_CAPACITY+at] = ev;
        ev.velocity.xyz = ParentEvents[i].velocity.xyz;
    }
}

bool IntersectBox(float3 start, float3 delta, float3 lo, float3 hi, float limit) {
    float3 safeDelta = float3(abs(delta.x)<1e-9 ? 1e-9 : delta.x,
                             abs(delta.y)<1e-9 ? 1e-9 : delta.y,
                             abs(delta.z)<1e-9 ? 1e-9 : delta.z);
    float3 a=(lo-start)/safeDelta,b=(hi-start)/safeDelta;
    float3 nearT=min(a,b),farT=max(a,b);
    return max(0,max(nearT.x,max(nearT.y,nearT.z))) <= min(limit,min(farT.x,min(farT.y,farT.z)));
}

bool SweepTriangle(float3 start, float3 delta, float3 a, float3 b, float3 c,
    float radius, inout float nearest, out float3 normal) {
    normal = SafeNormal(cross(b-a,c-a),float3(0,1,0));
    if (dot(start-a,normal) < 0) normal=-normal;
    float denom=dot(delta,normal);
    if (denom >= -1e-7) return false;
    float t=(radius-dot(start-a,normal))/denom;
    if (t < 0 || t > nearest) return false;
    float3 p=start+delta*t-normal*radius;
    float3 v0=b-a,v1=c-a,v2=p-a;
    float d00=dot(v0,v0),d01=dot(v0,v1),d11=dot(v1,v1),d20=dot(v2,v0),d21=dot(v2,v1);
    float det=d00*d11-d01*d01;
    if (abs(det)<1e-10) return false;
    float v=(d11*d20-d01*d21)/det,w=(d00*d21-d01*d20)/det;
    if (v < -1e-5 || w < -1e-5 || v+w > 1.00001) return false;
    nearest=t; return true;
}

bool Collide(inout float3 p, float3 old, inout float3 velocity) {
    if (Collision.x < 0.5) return false;
    float3 a=ToWorld(old), b=ToWorld(p), delta=b-a;
    float radius=max(0,Collision.w), nearest=1.0;
    float3 hitNormal=float3(0,1,0); bool hit=false;
    if (Options.y > 0.5 && b.y < Collision.y+radius) {
        nearest = a.y <= Collision.y+radius ? 0 : saturate((Collision.y+radius-a.y)/min(-1e-8,delta.y));
        hit=true;
    }
    uint node=0, nodes=(uint)Geometry.z, root=(uint)Geometry.y, triangles=(uint)Geometry.w;
    // Stackless preorder BVH: a miss jumps over the subtree, a hit advances into its children.
    // The finite node count bounds traversal; there is no silently truncated collision search.
    while(node<nodes) {
        float4 lo=Lookup[root+node*3], hi=Lookup[root+node*3+1], leaf=Lookup[root+node*3+2];
        if(!IntersectBox(a,delta,lo.xyz-radius,hi.xyz+radius,nearest)) { node=(uint)lo.w; continue; }
        for(uint n=0;n<(uint)leaf.x;n++) {
            uint at=triangles+((uint)hi.w+n)*3; float3 normal;
            if(SweepTriangle(a,delta,Lookup[at].xyz,Lookup[at+1].xyz,Lookup[at+2].xyz,radius,nearest,normal)) {
                hit=true; hitNormal=normal;
            }
        }
        node++;
    }
    if(!hit) return false;
    float3 worldVelocity=DirectionWorld(velocity);
    b=a+delta*nearest+hitNormal*0.0005;
    if(Options.y>0.5) b.y=max(b.y,Collision.y+radius);
    if(Collision.x<1.5) {
        worldVelocity=reflect(worldVelocity,hitNormal)*max(0,Collision.z);
        // Finish the remaining fraction after reflection, avoiding one-frame hovering.
        b+=worldVelocity*Timing.x*(1-nearest);
    } else worldVelocity=0;
    p=FromWorld(b); velocity=DirectionLocal(worldVelocity);
    return true;
}

[numthreads(THREADS,1,1)]
void UpdateScan(uint3 tid : SV_DispatchThreadID, uint3 gid : SV_GroupID, uint lane : SV_GroupIndex) {
    uint i=tid.x; uint alive=0,free=0,eventFlag=0;
    if(i<Capacity) {
        Particle p=State[i]; float previousAge=p.positionAge.w;
        p.previousEvent=float4(p.positionAge.xyz,0);
        bool wasAlive=p.velocityLife.w>0 && previousAge<p.velocityLife.w;
        bool tail=Render.x>0.5 && Render.x<1.5;
        bool occupied=p.velocityLife.w>0 && previousAge<p.velocityLife.w+(tail?Render.z:0);
        if(occupied) {
            p.positionAge.w += Timing.x;
            if(wasAlive && p.positionAge.w>=p.velocityLife.w) { eventFlag|=2; InterlockedAdd(Pool[5],1); }
            if(p.positionAge.w<p.velocityLife.w) {
                float t=saturate(p.positionAge.w/p.velocityLife.w);
                float4 curve=Curve(t,0);
                float3 v=p.velocityLife.xyz+Forces.xyz*Timing.x;
                uint rng=Seed ^ Hash(asuint(p.identity.x)+Sequence*31337);
                v+=float3(Sym(rng),Sym(rng)*0.25,Sym(rng))*Wind.z*Timing.x;
                float speedScale=max(0.001,curve.y);
                v*=speedScale/max(0.001,p.rotationSpeedScale.z);
                p.rotationSpeedScale.z=speedScale;
                v*=max(0,1-Forces.w*Timing.x);
                float3 pos=p.positionAge.xyz+v*Timing.x*max(0,curve.w)+float3(Wind.x,0,Wind.y)*Timing.x;
                if(Collide(pos,p.positionAge.xyz,v)) {
                    eventFlag|=4; InterlockedAdd(Pool[6],1);
                    if(Collision.x>1.5 && Collision.x<2.5) {
                        p.positionAge.w=p.velocityLife.w; eventFlag|=2; InterlockedAdd(Pool[5],1);
                    }
                }
                p.positionAge.xyz=pos; p.velocityLife.xyz=v;
                p.rotationSpeedScale.x+=p.rotationSpeedScale.y*Timing.x;
                if(tail) {
                    p.identity.w += Timing.x;
                    if(p.identity.w>=max(0.001,Render.z/(TRAIL_SAMPLES-1))) {
                        [unroll] for(int n=TRAIL_SAMPLES-1;n>0;n--) p.trail[n]=p.trail[n-1];
                        p.identity.w=0;
                    }
                    p.trail[0]=float4(pos,p.positionAge.w);
                }
            }
            occupied=p.positionAge.w<p.velocityLife.w+(tail?Render.z:0);
        }
        p.previousEvent.w=(float)eventFlag; State[i]=p;
        alive=occupied?1:0; free=1-alive;
    }
    uint eventCount=eventFlag!=0?1:0;
    uint3 value=uint3(alive,free,eventCount); Scan[lane]=value;
    GroupMemoryBarrierWithGroupSync();
    [unroll] for(uint stride=1;stride<THREADS;stride*=2) {
        uint3 add=lane>=stride?Scan[lane-stride]:uint3(0,0,0);
        GroupMemoryBarrierWithGroupSync();
        Scan[lane]+=add;
        GroupMemoryBarrierWithGroupSync();
    }
    if(i<Capacity) {
        uint at=LOCAL_BASE+i*4;
        Pool[at]=alive; Pool[at+1]=Scan[lane].x-alive;
        Pool[at+2]=Scan[lane].y-free; Pool[at+3]=Scan[lane].z-eventCount;
    }
    if(lane==THREADS-1) {
        uint at=GROUP_BASE+gid.x*4;
        Pool[at]=Scan[lane].x; Pool[at+1]=Scan[lane].y; Pool[at+2]=Scan[lane].z;
    }
}

[numthreads(1,1,1)]
void PrefixGroups(uint3 tid : SV_DispatchThreadID) {
    uint3 totals=0;
    for(uint group=0;group<Groups;group++) {
        uint at=GROUP_BASE+group*4;
        uint3 next=uint3(Pool[at],Pool[at+1],Pool[at+2]);
        Pool[at]=totals.x; Pool[at+1]=totals.y; Pool[at+2]=totals.z;
        totals+=next;
    }
    uint requested=Pool[11]+min(Pool[10],EVENT_CAPACITY);
    uint born=min(requested,totals.y);
    Pool[1]=totals.y; Pool[2]=totals.x; Pool[3]=born;
    Pool[12]=Pool[4]; Pool[4]+=born; Pool[7]+=requested-born;
    Pool[9]=totals.z;
    Pool[8]=min(totals.z+born,EVENT_CAPACITY);
    Pool[16]+=totals.z+born-Pool[8];
    Pool[17]=Pool[13]; Pool[18]=Pool[14];
    Pool[0]=totals.x+born; Pool[15]=Sequence;
}

float3 Cone(float angle,inout uint seed) {
    float y=lerp(cos(clamp(angle,0,3.141593)),1,Random01(seed));
    float r=sqrt(max(0,1-y*y)),a=Random01(seed)*6.283185;
    return float3(cos(a)*r,y,sin(a)*r);
}
Particle Spawn(uint i,uint birth) {
    uint serial=Pool[12]+birth+1;
    uint rng=Hash(Seed ^ serial*747796405u);
    float3 p=0,d=float3(0,1,0); uint shape=(uint)Emission.x;
    float speed=Motion.x*(1+Sym(rng)*Motion.y),life=max(0.05,Motion.z*(1+Sym(rng)*Motion.w));
    if(shape==2) { float y=Sym(rng),a=Random01(rng)*6.283185,r=sqrt(max(0,1-y*y)); d=float3(cos(a)*r,y,sin(a)*r); }
    else if(shape==3 || shape==4) {
        float a=Random01(rng)*6.283185,r=Emission.y*(shape==3?sqrt(Random01(rng)):1);
        p=float3(cos(a)*r,0,sin(a)*r);
        d=shape==3?Cone(Emission.z*0.5,rng):SafeNormal(float3(-sin(a),0.6,cos(a))+SafeNormal(p,0)*(Emission.z/3.141593-1)*0.5,float3(0,1,0));
    } else if(shape==5) { p=float3(Sym(rng),Sym(rng),Sym(rng))*Box.xyz*0.5; d=Cone(Emission.z,rng); }
    else if(shape==6 && Box.w>0) { p=Lookup[(uint)Geometry.x+min((uint)(Random01(rng)*Box.w),(uint)Box.w-1)].xyz; d=SafeNormal(p,float3(0,1,0)); }
    else d=Cone(shape==0?0.06981317:Emission.z,rng);
    if(Emission.w>0.5) d.y=-abs(d.y);
    float3 velocity=d*speed;
    if(Options.x<0.5) { p=mul(float4(p,1),World).xyz; velocity=mul(float4(velocity,0),World).xyz; }
    if(birth>=Pool[11]) {
        ParticleEvent ev=Events[EVENT_CAPACITY+min(birth-Pool[11],EVENT_CAPACITY-1)];
        p+=Options.x>0.5?mul(float4(ev.positionMask.xyz,1),InverseWorld).xyz:ev.positionMask.xyz-World[3].xyz;
        velocity+=DirectionLocal(ev.velocity.xyz);
    }
    Particle result=(Particle)0;
    result.positionAge=float4(p,0); result.velocityLife=float4(velocity,life);
    result.rotationSpeedScale=float4(Sym(rng)*Rotation.y,Rotation.x*(1+Sym(rng)*0.3),1,Sym(rng)*Rotation.z);
    result.identity=float4(asfloat(serial),0,asfloat(0xffffffffu),0);
    [unroll]for(uint t=0;t<TRAIL_SAMPLES;t++)result.trail[t]=float4(p,0);
    return result;
}

void WriteEvent(uint at,Particle p,uint mask) {
    if(at>=EVENT_CAPACITY)return;
    ParticleEvent ev;ev.positionMask=float4(ToWorld(p.positionAge.xyz),(float)mask);
    ev.velocity=float4(DirectionWorld(p.velocityLife.xyz),0);Events[at]=ev;
}

[numthreads(THREADS,1,1)]
void SpawnCompact(uint3 tid : SV_DispatchThreadID,uint3 gid : SV_GroupID) {
    uint i=tid.x;if(i>=Capacity)return;
    uint local=LOCAL_BASE+i*4, group=GROUP_BASE+gid.x*4;
    Particle p=State[i];uint eventMask=(uint)p.previousEvent.w;
    if(eventMask!=0)WriteEvent(Pool[group+2]+Pool[local+3],p,eventMask);
    if(Pool[local]!=0)Pool[AliveBase()+Pool[group]+Pool[local+1]]=i;
    else {
        uint birth=Pool[group+1]+Pool[local+2];
        if(birth<Pool[3]) {
            p=Spawn(i,birth);State[i]=p;
            Pool[AliveBase()+Pool[2]+birth]=i;
            WriteEvent(Pool[9]+birth,p,1);
        }
    }
}

[numthreads(1,1,1)]
void DrawArguments(uint3 tid : SV_DispatchThreadID) { SetArgument(0,(uint)Limits.y); SetArgument(1,Pool[0]); }

[numthreads(THREADS,1,1)]
void FinishStep(uint3 tid : SV_DispatchThreadID) {
    uint birth=tid.x;
    if(birth<Pool[3]) {
        uint at=Pool[AliveBase()+Pool[2]+birth]; Particle p=State[at];
        uint prior=birth>0?Pool[AliveBase()+Pool[2]+birth-1]:Pool[17];
        uint priorSerial=birth>0?Pool[12]+birth:Pool[18];
        p.identity.y=asfloat(priorSerial);p.identity.z=asfloat(prior);State[at]=p;
        if(birth==Pool[3]-1){Pool[13]=at;Pool[14]=asuint(p.identity.x);}
    }
    if(birth==0) {
        SetArgument(0,(uint)Limits.y);SetArgument(1,Pool[0]);SetArgument(2,0);SetArgument(3,0);SetArgument(4,0);
        SetArgument(5,(Pool[0]+THREADS-1)/THREADS);SetArgument(6,1);SetArgument(7,1);
        SetArgument(8,(Pool[8]+THREADS-1)/THREADS);SetArgument(9,1);SetArgument(10,1);
    }
}
""";

    public static readonly string Draw = Common + """
StructuredBuffer<Particle> State : register(t0);
StructuredBuffer<uint> Pool : register(t1);
StructuredBuffer<float4> Lookup : register(t2);
Texture2D ParticleTexture : register(t3);
SamplerState LinearSampler : register(s0);
cbuffer DrawParameters : register(b2) {
    row_major float4x4 ViewProjection;
    float4 CameraRight,CameraUp,CameraForward;
    float4 Screen;
    float4 Mode;
};
struct VertexInput {float3 position:POSITION;float2 uv:TEXCOORD0;};
struct VertexOutput {float4 position:SV_Position;float2 uv:TEXCOORD0;float4 color:COLOR0;};
float4 Curve(float t,uint offset) {
    float x=saturate(t)*(CURVE_SAMPLES-1);uint a=(uint)x;
    return lerp(Lookup[offset+a],Lookup[offset+min(a+1,CURVE_SAMPLES-1)],frac(x));
}
float3 ScreenPosition(float3 p) {
    return Mode.x>0.5?float3(Screen.xy+p.xy*Screen.z,0):p;
}
VertexOutput VS(VertexInput input,uint vertex:SV_VertexID,uint instance:SV_InstanceID) {
    uint capacity=(uint)Mode.y;
    uint id=Pool[LOCAL_BASE+capacity*4+instance];Particle p=State[id];
    float age=p.positionAge.w,life=max(0.05,p.velocityLife.w),t=saturate(age/life);
    float4 curve=Curve(t,0),color=Curve(t,CURVE_SAMPLES);
    float size=lerp(Sizes.x,Sizes.y,curve.x),sampleAge=age;
    float3 centre=ToWorld(p.positionAge.xyz),right=CameraRight.xyz,up=CameraUp.xyz;
    float3 offset=0;float2 uv=input.uv;
    uint kind=(uint)Render.x,alignment=(uint)Render.y;
    if(kind==1 || kind==3) {
        uint sample=min(vertex/2,TRAIL_SAMPLES-1);float side=(vertex&1)==0?-0.5:0.5;
        float3 samplePosition,previous,next;
        if(kind==1) {
            samplePosition=ToWorld(p.trail[sample].xyz); sampleAge=p.trail[sample].w;
            previous=ToWorld(p.trail[sample==0?0:sample-1].xyz);
            next=ToWorld(p.trail[min(sample+1,TRAIL_SAMPLES-1)].xyz);
            color=Curve(saturate(sampleAge/life),CURVE_SAMPLES);
            color.a*=saturate(1-(age-sampleAge)/max(0.001,Render.z));
        } else {
            float f=sample/(float)(TRAIL_SAMPLES-1);
            float3 end=mul(float4(Beam.xyz,1),World).xyz;
            samplePosition=lerp(centre,end,f);
            float envelope=sin(f*3.141593)*Beam.w;
            samplePosition+=right*sin(f*31+Mode.w*13)*envelope+up*cos(f*19-Mode.w*9)*envelope;
            previous=centre;next=end;
        }
        float3 along=SafeNormal(next-previous,CameraUp.xyz);
        right=SafeNormal(cross(CameraForward.xyz,along),CameraRight.xyz);
        centre=samplePosition;offset=right*side*size*Options.z;
        uv=float2(side+0.5,sample/(float)(TRAIL_SAMPLES-1));
    } else if(kind==2) {
        uint prior=asuint(p.identity.z);bool valid=prior<capacity;
        Particle before=(Particle)0;
        if(valid) {before=State[prior];valid=asuint(before.identity.x)==asuint(p.identity.y) && before.positionAge.w<before.velocityLife.w;}
        float3 end=valid?ToWorld(before.positionAge.xyz):centre;
        valid=valid && distance(centre,end)<=max(0.001,Render.w);
        if(!valid)color.a=0;
        float3 along=SafeNormal(end-centre,CameraUp.xyz);
        right=SafeNormal(cross(CameraForward.xyz,along),CameraRight.xyz);
        centre=lerp(centre,end,input.uv.y);
        offset=right*(input.uv.x-0.5)*size*Options.z;
    } else if(alignment==3) {
        float a=p.rotationSpeedScale.x,s=sin(a),c=cos(a);
        float3 pos=input.position*float3(size*Sizes.z,size*Sizes.w,size*Sizes.z);
        offset=float3(pos.x*c+pos.z*s,pos.y,-pos.x*s+pos.z*c);
    } else {
        if(alignment==1 || alignment==4 || Emission.w>0.5) {
            up=SafeNormal(DirectionWorld(p.velocityLife.xyz),up);
            right=SafeNormal(cross(CameraForward.xyz,up),right);
        } else if(alignment==2) {right=float3(1,0,0);up=float3(0,0,1);}
        float a=p.rotationSpeedScale.x,s=sin(a),c=cos(a);
        float3 r=right*c-up*s,u=right*s+up*c;
        float stretch=alignment==4?1+length(p.velocityLife.xyz)*Options.w:1;
        offset=r*(input.uv.x-0.5)*size*Sizes.z+u*(0.5-input.uv.y)*size*Sizes.w*stretch;
    }
    color.rgb=saturate(color.rgb*(1+p.rotationSpeedScale.w))*max(0,1+Rotation.w);
    if(Mode.x>0.5){centre=ScreenPosition(centre);offset.xy*=Screen.w;offset.z=0;}
    VertexOutput output;output.position=mul(float4(centre+offset,1),ViewProjection);
    if(Mode.x>0.5)output.position.z=0;
    if(Flipbook.w>0.5 && kind==0) {
        uint columns=max(1,(uint)Flipbook.x),rows=max(1,(uint)Flipbook.y);
        uint frame=(uint)(age*max(0,Flipbook.z))%(columns*rows);
        uv=(uv+float2(frame%columns,frame/columns))/float2(columns,rows);
    }
    output.uv=uv;output.color=color;return output;
}
float4 PS(VertexOutput input):SV_Target0 {
    float4 pixel=ParticleTexture.Sample(LinearSampler,input.uv);
    float4 color=pixel*input.color;
    clip(color.a-0.0001);return color;
}
""";
}
