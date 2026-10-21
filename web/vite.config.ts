import {defineConfig} from 'vite'
import react from '@vitejs/plugin-react'
import {fileURLToPath} from 'node:url'
import {isAbsolute,relative,resolve} from 'node:path'
import {federation} from '@module-federation/vite'
import tailwindcss from '@tailwindcss/vite'

export default defineConfig(({command})=>{
  const localPreview=previewDirectory()
  return ({
  publicDir:command==='serve'&&localPreview?localPreview:false,
  resolve:{dedupe:['react','react-dom']},
  plugins:[react(),tailwindcss(), ...(command==='build' ? [federation({
    name:'pixoo',filename:'remoteEntry.js',exposes:{'.':'./src/index.tsx'},
    shared:{react:{singleton:true,requiredVersion:false},'react/jsx-runtime':{singleton:true,requiredVersion:false},
      'react-dom':{singleton:true,requiredVersion:false},'react-dom/client':{singleton:true,requiredVersion:false},
      '@redbamboo/ui':{singleton:true,requiredVersion:false},
      '@redbamboo/utility':{singleton:true,requiredVersion:false}},dts:false,
  })] : [])],
  define:{
    'process.env.NODE_ENV':JSON.stringify(command==='build'?'production':'development'),
    __PIXOO_HAS_LOCAL_PREVIEW__:JSON.stringify(command==='serve'&&Boolean(localPreview)),
  },
  build:{outDir:process.env.PIXOO_WEB_OUTPUT ?? 'dist',emptyOutDir:true,target:'es2023',
    lib:{entry:fileURLToPath(new URL('./src/index.tsx',import.meta.url)),formats:['es'],fileName:()=> 'plugin.js',cssFileName:'plugin'},
    rollupOptions:{output:{chunkFileNames:'chunks/[name]-[hash].js'}}},
})})

function previewDirectory(){
  const configured=process.env.PIXOO_PREVIEW_DIR?.trim()
  if(!configured)return undefined
  const candidate=resolve(configured)
  const ignoredArtifacts=fileURLToPath(new URL('../artifacts',import.meta.url))
  const scratch=process.env.REDLEAF_SCRATCH_DIR?.trim()
  if(within(ignoredArtifacts,candidate)||scratch&&isAbsolute(scratch)&&within(resolve(scratch),candidate))return candidate
  throw new Error('PIXOO_PREVIEW_DIR must be inside the ignored artifacts directory or REDLEAF_SCRATCH_DIR.')
}

function within(root:string,candidate:string){
  const path=relative(resolve(root),resolve(candidate))
  return path===''||!path.startsWith('..')&&!isAbsolute(path)
}
