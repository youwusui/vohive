/// <reference types="vite/client" />

declare module '*.vue' {
  import type { DefineComponent } from 'vue'
  const component: DefineComponent<object, object, any>
  export default component
}

declare module 'vue-virtual-scroller' {
  import type { DefineComponent } from 'vue'

  // RecycleScroller 的 props 类型
  interface RecycleScrollerProps<T = any> {
    items: T[]
    itemSize?: number | null
    keyField?: string
    direction?: 'vertical' | 'horizontal'
    buffer?: number
    pageMode?: boolean
    prerender?: number
    emitUpdate?: boolean
  }

  // RecycleScroller 的 slots 类型
  interface RecycleScrollerSlots<T = any> {
    default: (props: { item: T; index: number; active: boolean }) => any
    before?: () => any
    after?: () => any
  }

  export const RecycleScroller: new <T = any>() => {
    $props: RecycleScrollerProps<T>
    $slots: RecycleScrollerSlots<T>
  }

  export const DynamicScroller: DefineComponent<any, any, any>
  export const DynamicScrollerItem: DefineComponent<any, any, any>
}

declare module '@vicons/fluent' {
  import type { DefineComponent } from 'vue'
  export const Add20Regular: DefineComponent<any, any, any>
  export const Add24Regular: DefineComponent<any, any, any>
  export const Alert24Regular: DefineComponent<any, any, any>
  export const ArrowDownload24Regular: DefineComponent<any, any, any>
  export const ArrowRight24Regular: DefineComponent<any, any, any>
  export const ArrowSync24Regular: DefineComponent<any, any, any>
  export const Board24Regular: DefineComponent<any, any, any>
  export const Call24Regular: DefineComponent<any, any, any>
  export const Cellular3G24Regular: DefineComponent<any, any, any>
  export const Cellular4G24Regular: DefineComponent<any, any, any>
  export const Cellular5G24Regular: DefineComponent<any, any, any>
  export const CellularData124Regular: DefineComponent<any, any, any>
  export const Code24Regular: DefineComponent<any, any, any>
  export const Delete20Regular: DefineComponent<any, any, any>
  export const Delete24Regular: DefineComponent<any, any, any>
  export const DocumentText24Regular: DefineComponent<any, any, any>
  export const Earth24Regular: DefineComponent<any, any, any>
  export const Edit24Regular: DefineComponent<any, any, any>
  export const Eye24Regular: DefineComponent<any, any, any>
  export const EyeOff24Regular: DefineComponent<any, any, any>
  export const Globe24Regular: DefineComponent<any, any, any>
  export const Key24Regular: DefineComponent<any, any, any>
  export const Link24Regular: DefineComponent<any, any, any>
  export const LockClosed24Regular: DefineComponent<any, any, any>
  export const Mail24Regular: DefineComponent<any, any, any>
  export const Pause24Regular: DefineComponent<any, any, any>
  export const Person24Regular: DefineComponent<any, any, any>
  export const Phone24Regular: DefineComponent<any, any, any>
  export const Play24Regular: DefineComponent<any, any, any>
  export const Power24Regular: DefineComponent<any, any, any>
  export const Router24Regular: DefineComponent<any, any, any>
  export const Save24Regular: DefineComponent<any, any, any>
  export const Send24Regular: DefineComponent<any, any, any>
  export const Server24Regular: DefineComponent<any, any, any>
  export const Settings24Regular: DefineComponent<any, any, any>
  export const SignOut24Regular: DefineComponent<any, any, any>
  export const Sim24Regular: DefineComponent<any, any, any>
  export const Stop24Regular: DefineComponent<any, any, any>
  export const Warning24Regular: DefineComponent<any, any, any>
  export const WeatherMoon24Regular: DefineComponent<any, any, any>
  export const WeatherSunny24Regular: DefineComponent<any, any, any>
  export const Wifi124Regular: DefineComponent<any, any, any>
  const icons: Record<string, DefineComponent<any, any, any>>
  export default icons
}

declare module '*.css' {
  const content: string
  export default content
}
