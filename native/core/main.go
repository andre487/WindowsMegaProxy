package main

/*
#cgo windows LDFLAGS: -lwintrust -lcrypt32
#include <stdlib.h>
int megaproxy_authorize_host(void);
*/
import "C"

import (
	"context"
	"errors"
	"fmt"
	"sync"
	"unsafe"

	box "github.com/sagernet/sing-box"
	"github.com/sagernet/sing-box/option"
	"github.com/sagernet/sing/common/json"
)

var runtimeState struct {
	sync.Mutex
	instance *box.Box
	cancel   context.CancelFunc
}

func execute(operation func() error) (result *C.char) {
	defer func() {
		if recovered := recover(); recovered != nil {
			result = C.CString(fmt.Sprintf("Network core panic: %v", recovered))
		}
	}()
	if C.megaproxy_authorize_host() != 1 {
		return C.CString("The host executable is not signed by the MegaProxy signing key.")
	}
	runtimeState.Lock()
	defer runtimeState.Unlock()
	if err := operation(); err != nil {
		return C.CString(err.Error())
	}
	return nil
}

func create(config string) (*box.Box, context.CancelFunc, error) {
	ctx, cancel := context.WithCancel(coreContext(context.Background()))
	options, err := json.UnmarshalExtendedContext[option.Options](ctx, []byte(config))
	if err != nil {
		cancel()
		return nil, nil, err
	}
	if options.Log == nil {
		options.Log = &option.LogOptions{}
	}
	options.Log.DisableColor = true
	instance, err := box.New(box.Options{Context: ctx, Options: options})
	if err != nil {
		cancel()
		return nil, nil, err
	}
	return instance, cancel, nil
}

//export MegaProxyCheck
func MegaProxyCheck(config *C.char) *C.char {
	return execute(func() error {
		if config == nil {
			return errors.New("Configuration is required.")
		}
		instance, cancel, err := create(C.GoString(config))
		if err != nil {
			return err
		}
		defer cancel()
		return instance.Close()
	})
}

//export MegaProxyStart
func MegaProxyStart(config *C.char) *C.char {
	return execute(func() error {
		if runtimeState.instance != nil {
			return errors.New("The network core is already running.")
		}
		if config == nil {
			return errors.New("Configuration is required.")
		}
		instance, cancel, err := create(C.GoString(config))
		if err != nil {
			return err
		}
		if err = instance.Start(); err != nil {
			cancel()
			_ = instance.Close()
			return err
		}
		runtimeState.instance, runtimeState.cancel = instance, cancel
		return nil
	})
}

//export MegaProxyStop
func MegaProxyStop() *C.char {
	return execute(func() error {
		if runtimeState.instance == nil {
			return nil
		}
		instance := runtimeState.instance
		runtimeState.cancel()
		runtimeState.instance, runtimeState.cancel = nil, nil
		return instance.Close()
	})
}

//export MegaProxyFree
func MegaProxyFree(value *C.char) { C.free(unsafe.Pointer(value)) }

func main() {}
