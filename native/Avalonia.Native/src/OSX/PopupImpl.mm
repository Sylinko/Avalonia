//
// Created by Dan Walmsley on 06/05/2022.
// Copyright (c) 2022 Avalonia. All rights reserved.
//

#include "WindowInterfaces.h"
#include "AvnView.h"
#include "WindowImpl.h"
#include "automation.h"
#include "menu.h"
#include "common.h"
#import "WindowBaseImpl.h"
#import "WindowProtocol.h"
#import <AppKit/AppKit.h>

class PopupImpl : public virtual WindowBaseImpl, public IAvnPopup
{
private:
    BEGIN_INTERFACE_MAP()
    INHERIT_INTERFACE_MAP(WindowBaseImpl)
    INTERFACE_MAP_ENTRY(IAvnPopup, IID_IAvnPopup)
    END_INTERFACE_MAP()
    virtual ~PopupImpl(){}
    ComPtr<IAvnWindowEvents> WindowEvents;
    PopupImpl(IAvnWindowEvents* events, bool isNonActivating) : TopLevelImpl(events), WindowBaseImpl(events, isNonActivating, isNonActivating)
    {
        WindowEvents = events;
        if (isNonActivating)
        {
            // Avalonia draws popup shadows within its surface. NSPanel otherwise
            // adds a native rectangular shadow that borderless NSWindow does not.
            [Window setHasShadow:false];
        }
        UpdateWindowLevel();
    }
protected:
    virtual NSWindowLevel GetBaseWindowLevel() override
    {
        return NSPopUpMenuWindowLevel;
    }

    virtual NSWindowStyleMask CalculateStyleMask() override
    {
        return NSWindowStyleMaskBorderless | ([Window styleMask] & NSWindowStyleMaskNonactivatingPanel);
    }

public:
    virtual HRESULT Show(bool activate, bool isDialog) override
    {
        auto windowProtocol = GetWindowProtocol();
        
        [windowProtocol setEnabled:true];
        
        return WindowBaseImpl::Show(activate, true);
    }
    
    virtual HRESULT SetHitTestVisible(bool value) override
    {
        START_COM_CALL;

        @autoreleasepool
        {
            [Window setIgnoresMouseEvents:!value];
            return S_OK;
        }
    }

    virtual bool ShouldTakeFocusOnShow() override
    {
        // The parent panel already owns keyboard/IME input. Showing its popup must
        // neither replace that key route nor activate the application.
        if (IsNonActivating())
            return false;

        auto parent = Parent.tryGet();
        // Don't steal the focus from another windows if our parent is inactive
        if (parent != nullptr && parent->Window != nullptr && ![parent->Window isKeyWindow])
            return false;

        return WindowBaseImpl::ShouldTakeFocusOnShow();
    }
};


extern IAvnPopup* CreateAvnPopup(IAvnWindowEvents*events)
{
    @autoreleasepool
    {
        IAvnPopup* ptr = dynamic_cast<IAvnPopup*>(new PopupImpl(events, false));
        return ptr;
    }
}

extern IAvnPopup* CreateAvnNonActivatingPopup(IAvnWindowEvents*events)
{
    @autoreleasepool
    {
        return dynamic_cast<IAvnPopup*>(new PopupImpl(events, true));
    }
}
