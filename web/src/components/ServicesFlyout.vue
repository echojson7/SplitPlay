<!-- SPDX-License-Identifier: MIT -->
<!-- "Services" button next to the app name and its flyout: the Windows services SplitPlay depends on, their state,
     and Start / Stop. The list refreshes every few seconds while the flyout is open. -->
<script setup lang="ts">
import { computed, onUnmounted, ref, watch } from 'vue'
import { api, ApiError } from '../api/client'
import type { HostService } from '../api/types'
import { t, type MessageKey } from '../i18n'
import { toast } from '../toast'
import AppIcon from './AppIcon.vue'
import ConfirmDialog from './ConfirmDialog.vue'

const POLL_MS = 3000

/** Services whose stop needs a confirmation, with its texts. */
const CONFIRM: Record<string, { title: MessageKey; text: MessageKey }> = {
  SplitPlay: { title: 'services.confirmSplitPlay', text: 'services.confirmSplitPlayText' },
  TermService: { title: 'services.confirmTermService', text: 'services.confirmTermServiceText' },
}

const LABELS: Record<string, MessageKey> = {
  SplitPlay: 'service.SplitPlay',
  TermService: 'service.TermService',
  UmRdpService: 'service.UmRdpService',
  ViGEmBus: 'service.ViGEmBus',
  Audiosrv: 'service.Audiosrv',
  'Steam Client Service': 'service.steam',
}

const open = ref(false)
const wrap = ref<HTMLElement | null>(null)
const services = ref<HostService[] | null>(null)
const loadError = ref<string | null>(null)
const busy = ref<string | null>(null)
const confirming = ref<HostService | null>(null)
let timer: ReturnType<typeof setInterval> | undefined

const confirmTexts = computed(() => (confirming.value ? CONFIRM[confirming.value.name] : undefined))

function label(s: HostService): string {
  const key = LABELS[s.name]
  return key ? t(key) : s.displayName || s.name
}

function statusText(s: HostService): string {
  if (s.status === 'stopped' && s.startType === 'disabled') return t('serviceStatus.disabled')
  return t(`serviceStatus.${s.status}` as MessageKey)
}

function tone(s: HostService): string {
  if (s.status === 'running') return 'ok'
  if (s.status === 'starting' || s.status === 'stopping') return 'busy'
  return ''
}

async function load(): Promise<void> {
  try {
    services.value = await api.listServices()
    loadError.value = null
  } catch (e) {
    loadError.value = e instanceof ApiError && e.status !== 0 ? e.message : t('app.offline')
  }
}

function replace(updated: HostService): void {
  if (!services.value) return
  services.value = services.value.map((s) => (s.name === updated.name ? updated : s))
}

async function run(s: HostService, action: 'start' | 'stop'): Promise<void> {
  busy.value = s.name
  try {
    replace(action === 'start' ? await api.startService(s.name) : await api.stopService(s.name))
  } catch (e) {
    const message = e instanceof Error ? e.message : String(e)
    toast(t(action === 'start' ? 'services.startFailed' : 'services.stopFailed', { name: label(s), message }), 'error')
    await load()
  } finally {
    busy.value = null
  }
}

function stop(s: HostService): void {
  if (CONFIRM[s.name]) confirming.value = s
  else void run(s, 'stop')
}

function confirmStop(): void {
  const s = confirming.value
  confirming.value = null
  if (s) void run(s, 'stop')
}

function onOutside(e: Event): void {
  if (confirming.value) return
  if (!wrap.value?.contains(e.target as Node)) open.value = false
}

function onKey(e: KeyboardEvent): void {
  if (e.key === 'Escape' && !confirming.value) open.value = false
}

watch(open, (isOpen) => {
  clearInterval(timer)
  if (isOpen) {
    void load()
    timer = setInterval(() => void load(), POLL_MS)
    document.addEventListener('pointerdown', onOutside)
    document.addEventListener('keydown', onKey)
  } else {
    document.removeEventListener('pointerdown', onOutside)
    document.removeEventListener('keydown', onKey)
  }
})

onUnmounted(() => {
  open.value = false
  clearInterval(timer)
  document.removeEventListener('pointerdown', onOutside)
  document.removeEventListener('keydown', onKey)
})
</script>

<template>
  <div ref="wrap" class="svc-wrap">
    <button
      type="button"
      class="btn-ghost svc-btn"
      data-services-button
      aria-haspopup="dialog"
      :aria-expanded="open"
      @click="open = !open"
    >
      <span>{{ t('services.button') }}</span>
      <AppIcon name="chevronDown" :size="12" />
    </button>

    <div v-if="open" class="flyout" role="dialog" :aria-label="t('services.button')" data-services-flyout>
      <div v-if="loadError" class="load-error" role="alert">{{ loadError }}</div>
      <div v-if="services" class="list">
        <div v-for="s in services" :key="s.name" class="svc" :data-service="s.name">
          <div class="text">
            <span class="name" :title="s.displayName">{{ label(s) }}</span>
            <span :class="['status', 'small', tone(s)]">{{ statusText(s) }}</span>
          </div>
          <button
            v-if="s.canStop"
            type="button"
            data-action="stop"
            :disabled="busy === s.name"
            @click="stop(s)"
          >
            {{ t('services.stop') }}
          </button>
          <button
            v-else-if="s.canStart"
            type="button"
            data-action="start"
            :disabled="busy === s.name"
            @click="run(s, 'start')"
          >
            {{ t('services.start') }}
          </button>
        </div>
      </div>
      <div v-else-if="!loadError" class="loading">{{ t('common.loading') }}</div>
    </div>

    <Teleport to="body">
      <ConfirmDialog
        :open="confirming !== null"
        :title="confirmTexts ? t(confirmTexts.title) : ''"
        :confirm-label="t('services.stop')"
        @confirm="confirmStop"
        @close="confirming = null"
      >
        <p v-if="confirmTexts">{{ t(confirmTexts.text) }}</p>
      </ConfirmDialog>
    </Teleport>
  </div>
</template>

<style scoped>
.svc-wrap {
  position: relative;
  flex: none;
}

.svc-btn {
  height: 28px;
  padding: 0 8px;
  gap: 6px;
  font-size: 12px;
  color: var(--text-2);
}

.svc-btn[aria-expanded='true'] {
  background: var(--subtle-hover);
  color: var(--text);
}

.flyout {
  position: absolute;
  top: calc(100% + 4px);
  left: 0;
  z-index: 40;
  width: 380px;
  max-width: calc(100vw - 16px);
  padding: 4px;
  background: var(--flyout);
  border: 1px solid var(--border);
  border-radius: 8px;
  box-shadow: var(--shadow-flyout);
}

.list {
  display: flex;
  flex-direction: column;
}

.svc {
  display: flex;
  align-items: center;
  gap: 12px;
  min-height: 56px;
  padding: 8px 12px;
  border-radius: 4px;
}

.svc + .svc {
  border-top: 1px solid var(--divider);
  border-radius: 0;
}

.text {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.name {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.status.small {
  gap: 6px;
  font-size: 12px;
  line-height: 16px;
  color: var(--text-2);
}

.status.small::before {
  width: 6px;
  height: 6px;
}

.svc > button {
  flex: none;
  min-width: 104px;
}

.load-error,
.loading {
  padding: 12px;
  font-size: 12px;
  line-height: 16px;
  color: var(--text-2);
}

.load-error {
  color: var(--bad);
}

@media (width <= 640px) {
  .flyout {
    position: fixed;
    top: 52px;
    left: 8px;
    right: 8px;
    width: auto;
  }
}
</style>
