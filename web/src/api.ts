import type {Agent,Device,FaceGesture,FaceState,Frame,Snapshot} from './types'
import {createExecutionTokenClient} from '@redbamboo/utility'

const base='/api/extensions/pixoo'
const client=createExecutionTokenClient('pixoo')
const harness=location.port==='4173' || new URLSearchParams(location.search).has('harness')
const ids={device:'11111111-1111-1111-1111-111111111111',agent:'22222222-2222-2222-2222-222222222222'}
const agents:Agent[]=[{id:ids.agent,name:'Juniper',color:'#78e6c4'},{id:'33333333-3333-3333-3333-333333333333',name:'Vesper',color:'#9a7cff'}]
let device:Device={id:ids.device,name:'Living room',kind:'emulator',brightness:74,agentId:ids.agent,agentColor:'#78e6c4',minimumPushGapMs:1000,hardwareEnabled:false}
let snapshot:Snapshot={device,state:'resting',frames:render('resting',device.agentColor,device.brightness),history:[{pushedAt:new Date().toISOString(),state:'resting',cause:'device.created',frameCount:4}]}

async function json<T>(path:string,init?:RequestInit):Promise<T>{
  const response=await client.fetch(base+path,{...init,headers:{'Content-Type':'application/json',...(init?.headers??{})}})
  if(!response.ok)throw new Error(`${response.status} ${response.statusText}`)
  return response.json() as Promise<T>
}
export async function listAgents(){return harness?agents:json<Agent[]>('/agents')}
export async function listDevices(){return harness?[device]:json<Device[]>('/devices')}
export async function current(deviceId:string){return harness?snapshot:json<Snapshot>(`/emulator?deviceId=${encodeURIComponent(deviceId)}`)}
export async function updateDevice(value:Device){
  if(harness){device={...value,agentColor:agents.find(a=>a.id===value.agentId)?.color??value.agentColor};snapshot={...snapshot,device};return device}
  return json<Device>(`/devices/${value.id}`,{method:'PUT',body:JSON.stringify(value)})
}
export async function createDevice(agentId:string){
  const value={name:'Living room emulator',kind:'emulator' as const,brightness:74,agentId,agentColor:agents.find(a=>a.id===agentId)?.color??'#7c5cff',minimumPushGapMs:1000,hardwareEnabled:false}
  if(harness){device={id:ids.device,...value};snapshot={device,state:'resting',frames:render('resting',device.agentColor,device.brightness),history:[]};return device}
  return json<Device>('/devices',{method:'POST',body:JSON.stringify(value)})
}
export async function simulate(deviceId:string,state:FaceState,gesture?:FaceGesture){
  if(harness){
    snapshot={device,state,gesture,frames:render(state,device.agentColor,device.brightness,gesture),history:[{pushedAt:new Date().toISOString(),state,gesture,cause:gesture??'simulate',frameCount:4},...snapshot.history].slice(0,8)}
    return snapshot
  }
  return json<Snapshot>(`/devices/${deviceId}/simulate`,{method:'POST',body:JSON.stringify({state,gesture})})
}

function render(state:FaceState,color:string,brightness:number,gesture?:FaceGesture):Frame[]{
  const levels=state==='dark'?[0]:state==='resting'?[.45,.53,.62,.53]:state==='speaking'?[.75,.95,.82,.95]:state==='thinking'?[.58,.74,.9,.74]:[.65,.82]
  const duration=state==='speaking'?180:state==='thinking'?280:state==='resting'?700:450
  return levels.map((level,phase)=>{
    const rgb=hex(color),bytes=new Uint8Array(64*64*3),light=brightness/100
    if(state!=='dark'){
      for(let y=0;y<64;y++)for(let x=0;x<64;x++){
        const dx=(x-31.5)/45,dy=(y-31.5)/45,v=Math.max(0,1-dx*dx-dy*dy),drift=.82+.18*Math.sin((x+y+phase*4)/13)
        put(bytes,x,y,rgb,.075*level*v*drift*light)
      }
      const blink=state==='resting'&&phase===levels.length-1,eyeH=state==='asleep'||blink?1:state==='listening'?8:6,shift=state==='thinking'?phase-2:0
      eye(bytes,20+shift,25,8,eyeH,rgb,level*light);eye(bytes,44+shift,25,8,eyeH,rgb,level*light)
      const mouthH=state==='speaking'?(phase%2?6:2):state==='asleep'?1:2
      for(let y=-Math.floor(mouthH/2);y<=Math.floor(mouthH/2);y++)for(let x=-9;x<=9;x++)if(Math.abs(x)+Math.abs(y)<10)put(bytes,32+x,42+y,rgb,level*light)
      if(gesture==='react')for(const [cx,cy] of [[8,12],[55,10]])for(let d=-4;d<=4;d++){put(bytes,cx+d,cy,rgb,light);put(bytes,cx,cy+d,rgb,light)}
    }
    let raw=''; for(let i=0;i<bytes.length;i+=8192)raw+=String.fromCharCode(...bytes.subarray(i,i+8192))
    return {pixels:btoa(raw),durationMs:duration}
  })
}
function hex(value:string):[number,number,number]{const n=parseInt(value.replace('#',''),16);return[(n>>16)&255,(n>>8)&255,n&255]}
function put(p:Uint8Array,x:number,y:number,c:[number,number,number],l:number){if(x<0||x>63||y<0||y>63)return;const i=(Math.round(y)*64+Math.round(x))*3;p[i]=c[0]*l;p[i+1]=c[1]*l;p[i+2]=c[2]*l}
function eye(p:Uint8Array,cx:number,cy:number,w:number,h:number,c:[number,number,number],l:number){for(let y=-h/2;y<=h/2;y++)for(let x=-w/2;x<=w/2;x++)if(x*x/(w*w)+y*y/Math.max(1,h*h)<=.28)put(p,cx+x,cy+y,c,l)}
