<!-- SPDX-License-Identifier: MIT -->
<!-- "Pair New Client": sends the PIN shown by Moonlight to POST /desks/{id}/pair. -->
<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import { api as defaultApi, ApiClient, ApiError } from '../api/client'
import { t } from '../i18n'

const props = withDefaults(defineProps<{ deskId: string; open: boolean; client?: ApiClient }>(), {
  client: () => defaultApi,
})
const emit = defineEmits<{ close: []; paired: [name: string] }>()

const dialog = ref<HTMLDialogElement | null>(null)
const pinInput = ref<HTMLInputElement | null>(null)
const pin = ref('')
const name = ref('')
const busy = ref(false)
const error = ref<string | null>(null)
const touched = ref(false)

const pinValid = computed(() => /^\d{4}$/.test(pin.value))

watch(
  () => props.open,
  async (open) => {
    const d = dialog.value
    if (open) {
      pin.value = ''
      name.value = ''
      error.value = null
      touched.value = false
      // showModal is missing in some test DOMs; fall back to the open attribute.
      if (d && typeof d.showModal === 'function' && !d.open) d.showModal()
      else d?.setAttribute('open', '')
      await nextTick()
      pinInput.value?.focus()
    } else if (d?.open) {
      if (typeof d.close === 'function') d.close()
      else d.removeAttribute('open')
    }
  },
  { flush: 'post' },
)

function onPinInput(e: Event): void {
  pin.value = (e.target as HTMLInputElement).value.replace(/\D/g, '').slice(0, 4)
}

async function submit(): Promise<void> {
  touched.value = true
  if (!pinValid.value || busy.value) return
  busy.value = true
  error.value = null
  const clientName = name.value.trim() || t('pair.namePlaceholder')
  try {
    await props.client.pair(props.deskId, { pin: pin.value, name: clientName })
    emit('paired', clientName)
    emit('close')
  } catch (e) {
    error.value = e instanceof ApiError ? e.message : String(e)
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <dialog ref="dialog" aria-labelledby="pair-title" @close="emit('close')" @cancel.prevent="emit('close')">
    <form method="dialog" novalidate @submit.prevent="submit">
      <h2 id="pair-title">{{ t('pair.title') }}</h2>
      <p class="muted">{{ t('pair.hint') }}</p>
      <div class="field">
        <label for="pair-pin">{{ t('pair.pin') }}</label>
        <input
          id="pair-pin"
          ref="pinInput"
          class="pin mono"
          name="pin"
          inputmode="numeric"
          autocomplete="one-time-code"
          maxlength="4"
          placeholder="0000"
          :value="pin"
          :aria-invalid="touched && !pinValid"
          @input="onPinInput"
        />
        <span v-if="touched && !pinValid" class="error">{{ t('pair.pinInvalid') }}</span>
      </div>
      <div class="field">
        <label for="pair-name">{{ t('pair.name') }}</label>
        <input id="pair-name" v-model="name" name="name" maxlength="64" :placeholder="t('pair.namePlaceholder')" />
      </div>
      <p v-if="error" class="error" role="alert">{{ t('common.error', { message: error }) }}</p>
      <div class="row">
        <div class="spacer" />
        <button type="button" @click="emit('close')">{{ t('common.cancel') }}</button>
        <button type="submit" class="primary" :disabled="busy">{{ t('pair.submit') }}</button>
      </div>
    </form>
  </dialog>
</template>

<style scoped>
h2 {
  margin-top: 0;
}

.pin {
  font-size: 1.6rem;
  letter-spacing: 0.4em;
  width: 8ch;
  text-align: center;
}
</style>
