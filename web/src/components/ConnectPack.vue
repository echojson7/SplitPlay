<!-- SPDX-License-Identifier: MIT -->
<!-- Moonlight settings recommended for a desk (GET /desks/{id}/client-pack) and its launch scripts for Windows and Linux. -->
<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { api as defaultApi, ApiClient } from '../api/client'
import type { ClientPack, ClientPlatform, Desk } from '../api/types'
import { copyText, errorText } from '../composables'
import { codecLabel } from '../desk'
import { fmtBitrate, t, type MessageKey } from '../i18n'
import { toast } from '../toast'
import AppIcon from './AppIcon.vue'

const props = withDefaults(defineProps<{ desk: Desk; address?: string; client?: ApiClient }>(), {
  address: '',
  client: () => defaultApi,
})

const pack = ref<ClientPack | null>(null)
const error = ref<string | null>(null)
const busy = ref(false)

// Reload whenever something the pack is built from changes.
const source = computed(() => {
  const p = props.desk.profile
  return [props.desk.id, props.desk.basePort, p.width, p.height, p.fps, p.bitrateKbps, p.codec, props.address].join('|')
})

let seq = 0
async function load(): Promise<void> {
  const mine = ++seq
  try {
    const next = await props.client.clientPack(props.desk.id, props.address || undefined)
    if (mine !== seq) return
    pack.value = next
    error.value = null
  } catch (e) {
    if (mine !== seq) return
    pack.value = null
    error.value = errorText(e)
  }
}
watch(source, load, { immediate: true })

function onOff(on: boolean): string {
  return t(on ? 'pack.on' : 'pack.off')
}

function word(prefix: 'pack.mode' | 'pack.decoderKind', value: string): string {
  const key = `${prefix}.${value}` as MessageKey
  const text = t(key)
  return text === key ? value : text
}

const rows = computed(() => {
  const s = pack.value?.settings
  if (!s) return []
  return [
    { key: 'resolution', label: t('pack.resolution'), value: `${s.width} × ${s.height} · ${t('unit.fps', { value: s.fps })}` },
    { key: 'bitrate', label: t('pack.bitrate'), value: fmtBitrate(s.bitrateKbps) },
    { key: 'codec', label: t('pack.codec'), value: s.codec === 'auto' ? t('pack.codecAuto') : codecLabel(s.codec) },
    { key: 'vsync', label: t('pack.vsync'), value: onOff(s.vsync) },
    { key: 'display', label: t('pack.displayMode'), value: word('pack.mode', s.displayMode) },
    { key: 'decoder', label: t('pack.decoder'), value: word('pack.decoderKind', s.videoDecoder) },
    { key: 'pacing', label: t('pack.framePacing'), value: onOff(s.framePacing) },
  ]
})

async function download(platform: ClientPlatform): Promise<void> {
  const file = pack.value?.scripts.find((x) => x.platform === platform)?.fileName
  if (!file || busy.value) return
  busy.value = true
  try {
    const blob = await props.client.clientScript(props.desk.id, platform, props.address || undefined)
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = file
    a.click()
    setTimeout(() => URL.revokeObjectURL(url), 1000)
  } catch (e) {
    toast(errorText(e), 'error')
  } finally {
    busy.value = false
  }
}

async function copyCommand(): Promise<void> {
  if (!pack.value) return
  const command = ['moonlight', ...pack.value.arguments].join(' ')
  toast((await copyText(command)) ? t('common.copied') : command)
}
</script>

<template>
  <div class="rows" data-pack>
    <div v-if="error" class="srow compact" data-pack-error>
      <AppIcon name="info" :size="16" />
      <span class="text muted">{{ error }}</span>
    </div>
    <div v-else-if="!pack" class="srow compact">
      <span class="row-pad" />
      <span class="text muted">{{ t('common.loading') }}</span>
    </div>
    <template v-else>
      <div v-for="(r, i) in rows" :key="r.key" class="srow compact">
        <AppIcon v-if="i === 0" name="monitor" :size="16" />
        <span v-else class="row-pad" />
        <div class="text"><span class="title">{{ r.label }}</span></div>
        <div class="ctl val" :data-pack-value="r.key">{{ r.value }}</div>
      </div>
      <div class="srow">
        <AppIcon name="archive" :size="16" />
        <div class="text"><span class="title">{{ t('pack.script') }}</span></div>
        <div class="ctl">
          <button type="button" data-script="windows" :disabled="busy" @click="download('windows')">Windows</button>
          <button type="button" data-script="linux" :disabled="busy" @click="download('linux')">Linux</button>
          <button type="button" class="btn-icon" data-copy-command :title="t('pack.copyCommand')" :aria-label="t('pack.copyCommand')" @click="copyCommand">
            <AppIcon name="copy" :size="16" />
          </button>
        </div>
      </div>
    </template>
  </div>
</template>

<style scoped>
.ctl.val {
  font-variant-numeric: tabular-nums;
  min-width: 0;
  flex: 0 1 auto;
  text-align: right;
}

.row-pad {
  width: 16px;
  flex: none;
}

@media (width <= 600px) {
  .srow > .icon,
  .row-pad {
    display: none;
  }
}
</style>
