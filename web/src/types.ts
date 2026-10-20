export type FaceState='dark'|'resting'|'listening'|'thinking'|'speaking'|'working'|'dreaming'|'asleep'
export type FaceGesture='arrive'|'react'
export interface Agent {id:string;name:string;color:string}
export interface Device {id:string;name:string;kind:'emulator'|'pixoo64';host?:string;brightness:number;agentId:string;agentColor:string;minimumPushGapMs:number;hardwareEnabled:boolean}
export interface Frame {pixels:string;durationMs:number}
export interface Receipt {pushedAt:string;state:FaceState;gesture?:FaceGesture;cause:string;frameCount:number}
export interface Snapshot {device:Device;state:FaceState;gesture?:FaceGesture;frames:Frame[];history:Receipt[]}
